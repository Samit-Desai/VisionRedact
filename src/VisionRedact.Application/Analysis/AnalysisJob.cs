using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.Tracking;
using VisionRedact.Domain.Detections;

namespace VisionRedact.Application.Analysis;

// registered as AnalysisOptions.Default in App.xaml.cs
public sealed record AnalysisOptions
{
    public static AnalysisOptions Default { get; } = new();

    // frames waiting between the reader and the AI. memory = about this many full frames, however long the video
    public int ChannelCapacity { get; init; } = 4;
}

// live counters for the Detections panel and Diagnostics, ok to read from any thread.
// FramesScanned = every frame the reader got past, skipped ones too. FramesAnalyzed = only the ones the AI ran on
public readonly record struct AnalysisStatistics(
    long FramesScanned,
    long FramesAnalyzed,
    long Detections,
    TimeSpan AverageInferenceTime,
    int ChannelCount,
    int ChannelCapacity,
    long TotalFrames);

// the AI scan: one pass over the whole video, two tasks joined by a small bounded channel.
// reader (ReadNext or Skip) -> Channel(capacity, Wait) -> AI task: Detect -> tracker (TrackId) -> AddFrameResults
// AnalysisService builds it, ProcessingJobManager runs it. own decoder, never seeks, never drops a chosen frame
// (dropping under load would make the coverage depend on the PC, ADR-0013)
public sealed class AnalysisJob : ProcessingJob
{
    private readonly string _videoPath;
    private readonly IVideoDecoderFactory _decoderFactory;
    private readonly IObjectDetector _detector;
    private readonly IDetectionRepository _repository;
    private readonly IFrameSamplingStrategy _sampling;
    private readonly IObjectTracker? _tracker;
    private readonly AnalysisOptions _options;
    private readonly TimeProvider _time;
    private Channel<OwnedVideoFrame>? _channel;

    // written by the two tasks, read by the UI (GetStatistics, Summary). always through Interlocked
    private long _scanned;
    private long _analyzed;
    private long _detections;
    private long _inferenceTicks;
    private long _totalFrames;

    // detector = a DetectorHost handle. the job disposes it at the end, which leaves the model loaded
    public AnalysisJob(
        string videoPath,
        IVideoDecoderFactory decoderFactory,
        IObjectDetector detector,
        IDetectionRepository repository,
        IFrameSamplingStrategy sampling,
        IObjectTracker? tracker = null,
        AnalysisOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<AnalysisJob>? logger = null)
        : base($"Analyze ({detector.Info.Name}, {sampling.Description})", timeProvider, logger)
    {
        _videoPath = videoPath;
        _decoderFactory = decoderFactory;
        _detector = detector;
        _repository = repository;
        _sampling = sampling;
        _tracker = tracker;
        _options = options ?? AnalysisOptions.Default;
        _time = timeProvider ?? TimeProvider.System;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ChannelCapacity, 1);
    }

    public IFrameSamplingStrategy Sampling => _sampling;

    public DetectorInfo Detector => _detector.Info;

    public AnalysisStatistics GetStatistics()
    {
        var analyzed = Interlocked.Read(ref _analyzed);
        var reader = _channel?.Reader;
        return new AnalysisStatistics(
            Interlocked.Read(ref _scanned),
            analyzed,
            Interlocked.Read(ref _detections),
            analyzed == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Interlocked.Read(ref _inferenceTicks) / analyzed),
            reader is { CanCount: true } ? reader.Count : 0,
            _options.ChannelCapacity,
            Interlocked.Read(ref _totalFrames));
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            // own decoder: the player has its own, so the scan never fights it over seeks.
            // a blocking native open, fine, we're on a pool thread
            using var decoder = _decoderFactory.Open(_videoPath);
            Interlocked.Exchange(ref _totalFrames, decoder.Metadata.FrameCount);
            // tell the store how long a scanned frame's boxes count, and number new tracks after the highest one
            // stored, so a new track never reuses the id of a reviewed box from an earlier scan (ADR-0024)
            _repository.HoldFrames = _sampling.HoldFrames;
            _tracker?.Reset(_repository.HighestTrackId + 1);

            // Wait = when it's full the reader waits (backpressure). on the CPU the AI is the slow side, and w/o a bound
            // decoded frames would pile up until memory runs out
            var channel = Channel.CreateBounded<OwnedVideoFrame>(new BoundedChannelOptions(_options.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true,
            });
            _channel = channel;

            // one linked token: Cancel stops both tasks, and a failing task cancels the other
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var producer = Task.Run(() => ProduceAsync(decoder, channel.Writer, linked));
            var consumer = Task.Run(() => ConsumeAsync(channel.Reader, linked));
            var both = Task.WhenAll(producer, consumer);
            try
            {
                await both.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (both.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) is { } root)
            {
                // one side failed and canceled the other. throw the real error, not the other side's cancel
                ExceptionDispatchInfo.Throw(root);
            }
            finally
            {
                // frames still in the channel (cancel / failure) are native memory, free them
                while (channel.Reader.TryRead(out var leftover))
                {
                    leftover.Dispose();
                }
            }

            // force: skip the throttle, so the UI shows the real final numbers
            ReportProgress(Interlocked.Read(ref _scanned), decoder.Metadata.FrameCount, Summary(), force: true);
        }
        finally
        {
            _detector.Dispose(); // only the handle, the model stays loaded
        }
    }

    // reader task: front to back. chosen frames are decoded and queued, the rest skipped cheaply.
    // completes the channel at the end, so the AI task knows to stop
    private async Task ProduceAsync(IVideoDecoder decoder, ChannelWriter<OwnedVideoFrame> writer, CancellationTokenSource linked)
    {
        var token = linked.Token;
        try
        {
            var total = decoder.Metadata.FrameCount;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var index = decoder.Position;
                if (!_sampling.ShouldAnalyze(index))
                {
                    // good find - most native frame allocations were for frames thrown away right after.
                    // Skip() still decodes (later frames need it) but makes no picture (docs/Performance.md, optimization 1)
                    if (!decoder.Skip())
                    {
                        break;
                    }

                    ReportProgress(Interlocked.Increment(ref _scanned), total, Summary());
                    continue;
                }

                var frame = decoder.ReadNext();
                if (frame is null)
                {
                    break; // end of stream. the container's frame count is only an estimate
                }

                try
                {
                    await writer.WriteAsync(frame, token).ConfigureAwait(false); // waits while the channel is full
                }
                catch
                {
                    frame.Dispose(); // never made it into the channel, so it's still ours
                    throw;
                }

                ReportProgress(Interlocked.Increment(ref _scanned), total, Summary());
            }

            // normal end. the AI task's await foreach finishes once the channel is empty
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            // failure or cancel: close the channel with the error and stop the AI task too
            writer.TryComplete(ex);
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    // AI task: Detect -> Pending Detections -> TrackIds -> store. always disposes the frame
    private async Task ConsumeAsync(ChannelReader<OwnedVideoFrame> reader, CancellationTokenSource linked)
    {
        var token = linked.Token;
        try
        {
            await foreach (var frame in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    // timed for the "ms/frame" in the Detections panel
                    var started = _time.GetTimestamp();
                    var candidates = _detector.Detect(frame, token);
                    Interlocked.Add(ref _inferenceTicks, _time.GetElapsedTime(started).Ticks);

                    IReadOnlyList<Detection> detections = candidates
                        .Select(c => Detection.Create(frame.FrameIndex, c.Box, c.Confidence, c.Class))
                        .ToArray();
                    if (_tracker is not null)
                    {
                        detections = _tracker.Assign(frame.FrameIndex, detections);
                    }

                    // the scan never draws. the player, preview blur and export all read the store
                    _repository.AddFrameResults(frame.FrameIndex, detections);
                    Interlocked.Add(ref _detections, detections.Count);
                    Interlocked.Increment(ref _analyzed);
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (Exception)
        {
            await linked.CancelAsync().ConfigureAwait(false); // else a reader waiting on a full channel waits forever
            throw;
        }
    }

    private string Summary() =>
        $"{Interlocked.Read(ref _analyzed):N0} frames analyzed, {Interlocked.Read(ref _detections):N0} detections";
}
