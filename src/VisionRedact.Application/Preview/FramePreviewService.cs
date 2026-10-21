using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.Workspace;

namespace VisionRedact.Application.Preview;

// the exact picture of one frame, from Request
public sealed record FramePreview(long FrameIndex, ImageBuffer Image);

// sizes and timing of the timeline previews. App.xaml.cs registers Default
public sealed record FramePreviewOptions
{
    public static FramePreviewOptions Default { get; } = new();

    // how long the pointer must rest before the exact frame is decoded
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    // tiles at least this far apart in video time
    public TimeSpan TileSpacing { get; init; } = TimeSpan.FromSeconds(2);

    // a long video gets wider spacing instead. memory = MaxTiles * one tile
    public int MaxTiles { get; init; } = 240;

    public int MaxWidth { get; init; } = 192;

    public int MaxHeight { get; init; } = 108;
}

// snapshot for the Diagnostics panel: what the hover preview costs on top of playback.
// the frame reader also serves the People tab thumbnails, so its decoder, frames and read time count those too.
// PeakFramesInFlight adds up each decoder's own peak, so it's an upper bound.
// tile lookup = median of the last 64, in ns.
// NOTE: it was a mean at first, and one slow JIT-cold first call ruled it. a median ignores that call
public readonly record struct FramePreviewStatistics(
    int TilesBuilt,
    int TilesPlanned,
    long TileBytes,
    bool StoryboardBuilding,
    TimeSpan StoryboardBuildTime,
    bool FrameReaderOpen,
    bool StoryboardDecoderOpen,
    int FramesInFlight,
    int PeakFramesInFlight,
    long TileLookups,
    double MedianTileLookupNanoseconds,
    long ExactRequests,
    long ExactShown,
    long ExactSuperseded,
    TimeSpan AverageFrameRead);

// timeline hover preview in two layers (ADR-0023):
// pointer moves -> NearestTile from the storyboard, no decode. pointer rests SettleDelay -> exact frame -> Ready
// every Request cancels the one before, so a sweep decodes nothing, only the frame the pointer rests on.
// the first version had only the exact layer: while the pointer moved every request got canceled in its wait,
// so the card changed only when the mouse stopped. the storyboard was added for the moving pointer
public sealed class FramePreviewService
{
    private readonly VideoWorkspace _workspace;
    private readonly IFrameImaging _imaging;
    private readonly TimeProvider _time;
    private readonly IProcessingJobManager? _jobs;
    private readonly IVideoDecoderFactory? _decoders;
    private readonly FramePreviewOptions _options;
    private readonly ILogger _logger;
    private readonly object _storyboardGate = new(); // job + storyboard change together under this

    // the newest request's token, swapped atomically so only it survives. old sources get canceled, not disposed:
    // Dispose could race w/ their delay or read registering a callback, and a CTS w/o timers holds nothing to free
    private CancellationTokenSource? _current;
    private Storyboard _storyboard = Storyboard.Empty;
    private StoryboardJob? _storyboardJob;

    // Interlocked counters: the UI thread writes lookups + requests, pool threads shown + superseded.
    // lookup times go in a small ring as raw TimeProvider timestamps, not TimeSpan ticks, so under 100 ns still shows
    private const int RecentLookups = 64;
    private readonly long[] _recentLookupTimes = new long[RecentLookups];
    private long _tileLookups;
    private long _exactRequests;
    private long _exactShown;
    private long _exactSuperseded;

    public FramePreviewService(
        VideoWorkspace workspace,
        IFrameImaging imaging,
        TimeProvider time,
        FramePreviewOptions? options = null,
        ILogger<FramePreviewService>? logger = null,
        IProcessingJobManager? jobs = null,
        IVideoDecoderFactory? decoders = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(imaging);
        ArgumentNullException.ThrowIfNull(time);
        _workspace = workspace;
        _imaging = imaging;
        _time = time;
        _options = options ?? FramePreviewOptions.Default;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _jobs = jobs;
        _decoders = decoders;
    }

    // on a pool thread, when the newest request's frame is decoded. a request canceled by then doesn't raise it
    public event EventHandler<FramePreview>? Ready;

    public Storyboard Storyboard => Volatile.Read(ref _storyboard);

    // PlayerViewModel calls it for each new video. drops the old storyboard, and a builder still running for the
    // previous video can't publish into the new one
    public void LoadStoryboard()
    {
        if (_jobs is null || _decoders is null || _workspace.CurrentSession?.Metadata.SourcePath is not { } path)
        {
            return;
        }

        StoryboardJob? previous;
        StoryboardJob job = null!;
        job = new StoryboardJob(path, _decoders, _imaging, _options, tile => Publish(job, tile));
        lock (_storyboardGate)
        {
            previous = _storyboardJob;
            _storyboardJob = job;
            Volatile.Write(ref _storyboard, Storyboard.Empty);
        }

        previous?.Cancel();
        _jobs.Start(job);
    }

    /// <summary>The built tile nearest to the frame, null while there is none. Never decodes.</summary>
    public StoryboardTile? NearestTile(long frameIndex)
    {
        var started = _time.GetTimestamp();
        var tile = Storyboard.Nearest(frameIndex);
        var elapsed = _time.GetTimestamp() - started;
        var count = Interlocked.Increment(ref _tileLookups);
        Volatile.Write(ref _recentLookupTimes[(count - 1) % RecentLookups], elapsed);
        return tile;
    }

    /// <summary>The exact picture of the frame, after SettleDelay. Replaces any request still pending.</summary>
    public void Request(long frameIndex)
    {
        Interlocked.Increment(ref _exactRequests);
        var request = new CancellationTokenSource();
        Interlocked.Exchange(ref _current, request)?.Cancel();
        _ = RunAsync(frameIndex, request.Token);
    }

    // pointer left the timeline, or PlayerViewModel is disposed
    public void Cancel() => Interlocked.Exchange(ref _current, null)?.Cancel();

    // for the Diagnostics panel, any thread
    public FramePreviewStatistics GetStatistics()
    {
        var storyboard = Storyboard;
        // written under _storyboardGate, but only reading here, so no lock
        var job = Volatile.Read(ref _storyboardJob);
        var reader = _workspace.FrameReader;
        var lookups = Interlocked.Read(ref _tileLookups);
        return new FramePreviewStatistics(
            storyboard.Count,
            job?.TilesPlanned ?? 0,
            storyboard.PixelBytes,
            job is { Completion.IsCompleted: false },
            job?.Elapsed ?? TimeSpan.Zero,
            reader?.IsDecoderOpen ?? false,
            job?.IsDecoderOpen ?? false,
            (reader?.FramesHeld ?? 0) + (job?.FramesHeld ?? 0),
            (reader?.PeakFramesHeld ?? 0) + (job?.PeakFramesHeld ?? 0),
            lookups,
            MedianLookupNanoseconds(lookups),
            Interlocked.Read(ref _exactRequests),
            Interlocked.Read(ref _exactShown),
            Interlocked.Read(ref _exactSuperseded),
            reader?.AverageReadTime ?? TimeSpan.Zero);
    }

    // as a double, b/c one lookup can be shorter than a 100 ns TimeSpan tick
    private double MedianLookupNanoseconds(long lookups)
    {
        var count = (int)Math.Min(lookups, RecentLookups);
        if (count == 0)
        {
            return 0;
        }

        var times = new long[count];
        for (var i = 0; i < count; i++)
        {
            times[i] = Volatile.Read(ref _recentLookupTimes[i]);
        }

        Array.Sort(times);
        var middle = count % 2 == 1 ? times[count / 2] : (times[(count / 2) - 1] + times[count / 2]) / 2.0;
        return middle * 1e9 / _time.TimestampFrequency;
    }

    // builder thread. the job check makes an old builder (previous video) harmless
    private void Publish(StoryboardJob job, StoryboardTile tile)
    {
        lock (_storyboardGate)
        {
            if (ReferenceEquals(job, _storyboardJob))
            {
                Volatile.Write(ref _storyboard, _storyboard.With(tile));
            }
        }
    }

    private async Task RunAsync(long frameIndex, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.SettleDelay, _time, cancellationToken).ConfigureAwait(false);
            if (_workspace.FrameReader is not { } reader)
            {
                return;
            }

            var image = await reader.ReadAsync(frameIndex, frame => _imaging.Resize(frame, null, _options.MaxWidth, _options.MaxHeight), cancellationToken).ConfigureAwait(false);
            if (image is null)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _exactSuperseded); // decoded, but the pointer had already moved on
                return;
            }

            Interlocked.Increment(ref _exactShown);
            Ready?.Invoke(this, new FramePreview(frameIndex, image));
        }
        catch (OperationCanceledException)
        {
            // superseded or canceled. mostly in the settle delay, so nothing was decoded at all
            Interlocked.Increment(ref _exactSuperseded);
        }
        catch (MediaException ex)
        {
            // a preview is a nice-to-have: a frame that won't decode just shows no picture
            _logger.LogDebug(ex, "Preview of frame {Frame} failed.", frameIndex);
        }
    }
}
