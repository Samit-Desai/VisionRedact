using Microsoft.Extensions.Time.Testing;
using VisionRedact.Application.Media;
using VisionRedact.Application.Preview;
using VisionRedact.Application.Tests.Fakes;
using VisionRedact.Application.Workspace;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Application.Tests.Preview;

// hover preview (ADR-0023): only the frame the pointer stops on gets decoded, a canceled hover shows nothing
public sealed class FramePreviewServiceTests : IAsyncDisposable
{
    private static readonly FramePreviewOptions Options = new() { SettleDelay = TimeSpan.FromMilliseconds(80) };
    private readonly FakeTimeProvider _time = new();
    private readonly List<FakeVideoDecoder> _decoders = [];
    private readonly VideoWorkspace _workspace;
    private readonly FramePreviewService _service;

    public FramePreviewServiceTests()
    {
        // each Open gets a fresh decoder (playback and the frame reader must never share one)
        _workspace = new VideoWorkspace(
            new FakeVideoDecoderFactory(_ =>
            {
                var decoder = new FakeVideoDecoder(frameCount: 200);
                lock (_decoders)
                {
                    _decoders.Add(decoder);
                }

                return decoder;
            }),
            new RecordingFrameSink(),
            TimeProvider.System);
        _service = new FramePreviewService(_workspace, new FakeImaging(), _time, Options);
    }

    [Fact]
    public async Task Sweeping_across_frames_decodes_only_the_one_the_pointer_rests_on()
    {
        await _workspace.OpenAsync("video.mp4");
        var ready = WaitForPreview();

        _service.Request(10);
        _time.Advance(TimeSpan.FromMilliseconds(30));
        _service.Request(20);
        _time.Advance(TimeSpan.FromMilliseconds(30));
        _service.Request(30);
        _time.Advance(Options.SettleDelay);

        var preview = await ready;
        Assert.Equal(30, preview.FrameIndex);
        Assert.Equal(30, preview.Image.Pixels[0]); // the fake imaging copies the frame's content marker
        Assert.Equal([30L], ReaderDecoder().CreatedFrames.Select(f => f.FrameIndex));
    }

    [Fact]
    public async Task Cancelled_hover_never_raises_ready()
    {
        await _workspace.OpenAsync("video.mp4");
        var raised = false;
        _service.Ready += (_, _) => raised = true;

        _service.Request(10);
        _service.Cancel();
        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50); // give any (wrongly) scheduled decode a chance to run

        Assert.False(raised);
    }

    [Fact]
    public async Task Without_an_open_video_nothing_is_decoded()
    {
        var raised = false;
        _service.Ready += (_, _) => raised = true;

        _service.Request(10);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.False(raised);
        Assert.Empty(_decoders);
    }

    [Fact]
    public async Task Statistics_count_each_request_once_as_shown_or_superseded()
    {
        await _workspace.OpenAsync("video.mp4");
        var ready = WaitForPreview();

        _service.Request(10);
        _time.Advance(TimeSpan.FromMilliseconds(30));
        _service.Request(20);
        _time.Advance(TimeSpan.FromMilliseconds(30));
        _service.Request(30);
        _time.Advance(Options.SettleDelay);
        await ready;

        var stats = await StatisticsWhen(s => s.ExactSuperseded == 2);
        Assert.Equal(3, stats.ExactRequests);
        Assert.Equal(1, stats.ExactShown);
        Assert.Equal(2, stats.ExactSuperseded);
        Assert.True(stats.FrameReaderOpen); // opened lazily by the one exact read
        Assert.False(stats.StoryboardDecoderOpen);
        Assert.Equal(0, stats.FramesInFlight); // released right after the read
        Assert.Equal(1, stats.PeakFramesInFlight);
    }

    [Fact]
    public async Task A_cancelled_hover_counts_as_superseded_and_opens_no_decoder()
    {
        await _workspace.OpenAsync("video.mp4");

        _service.Request(10);
        _service.Cancel();

        var stats = await StatisticsWhen(s => s.ExactSuperseded == 1);
        Assert.Equal(1, stats.ExactRequests);
        Assert.Equal(0, stats.ExactShown);
        Assert.Equal(1, stats.ExactSuperseded);
        Assert.False(stats.FrameReaderOpen); // nothing was decoded, so the lazy decoder never opened
        Assert.Equal(TimeSpan.Zero, stats.AverageFrameRead);
    }

    [Fact]
    public void Tile_lookups_are_counted_even_before_any_tile_exists()
    {
        Assert.Null(_service.NearestTile(5));
        _service.NearestTile(50);

        var stats = _service.GetStatistics();
        Assert.Equal(2, stats.TileLookups);
        Assert.Equal(0, stats.TilesBuilt);
        Assert.Equal(0, stats.TileBytes);
        Assert.False(stats.StoryboardBuilding);
        Assert.False(stats.FrameReaderOpen);
        Assert.False(stats.StoryboardDecoderOpen);
    }

    [Fact]
    public void Tile_lookup_time_is_a_median_that_ignores_one_slow_call()
    {
        // 1 timestamp unit = 100 ns. lookups take 1, 100 (one slow call, e.g. JIT) and 1 units:
        // the median is 100 ns, a mean would say 3,400 ns
        var service = new FramePreviewService(_workspace, new FakeImaging(), new ScriptedTimestamps(0, 1, 5, 105, 200, 201), Options);
        Assert.Equal(0, service.GetStatistics().MedianTileLookupNanoseconds); // no lookups yet: 0, not NaN

        service.NearestTile(5);
        service.NearestTile(50);
        service.NearestTile(500);

        Assert.Equal(100, service.GetStatistics().MedianTileLookupNanoseconds, precision: 6);
    }

    [Fact]
    public void Tile_lookup_median_keeps_times_shorter_than_one_timespan_tick()
    {
        // lookups of 1 and 0 units: the median is 50 ns, a TimeSpan (whole 100 ns ticks) would round it to 0
        var service = new FramePreviewService(_workspace, new FakeImaging(), new ScriptedTimestamps(0, 1, 5, 5), Options);

        service.NearestTile(5);
        service.NearestTile(50);

        Assert.Equal(50, service.GetStatistics().MedianTileLookupNanoseconds, precision: 6);
    }

    public async ValueTask DisposeAsync() => await _workspace.DisposeAsync();

    // a superseded request is counted on whichever thread finishes its cancel, so poll briefly instead of sleeping
    private async Task<FramePreviewStatistics> StatisticsWhen(Func<FramePreviewStatistics, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stats = _service.GetStatistics();
        while (!condition(stats) && !timeout.IsCancellationRequested)
        {
            await Task.Delay(10);
            stats = _service.GetStatistics();
        }

        return stats;
    }

    private Task<FramePreview> WaitForPreview()
    {
        var done = new TaskCompletionSource<FramePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Ready += (_, preview) => done.TrySetResult(preview);
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // decoder #1 is playback's, the frame reader opens #2 on its first read
    private FakeVideoDecoder ReaderDecoder()
    {
        lock (_decoders)
        {
            return _decoders[1];
        }
    }

    // stands in for EmguFrameImaging: a 1x1 "image" holding the frame's marker byte
    private sealed class FakeImaging : IFrameImaging
    {
        public ImageBuffer Resize(OwnedVideoFrame frame, NormalizedRect? region, int maxWidth, int maxHeight) =>
            new([frame.GetPixels()[0], 0, 0], 1, 1, 3);
    }

    // each GetTimestamp returns the next scripted value, so the test decides how long a lookup "took"
    private sealed class ScriptedTimestamps(params long[] timestamps) : TimeProvider
    {
        private int _next;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond; // 1 unit = 100 ns

        public override long GetTimestamp() => timestamps[_next++];
    }
}
