using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.Preview;
using VisionRedact.Application.Tests.Fakes;
using VisionRedact.Application.Workspace;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Application.Tests.Preview;

public class StoryboardTests
{
    private static readonly ImageBuffer Pixel = new([0, 0, 0], 1, 1, 3);

    [Fact]
    public void Plan_spaces_tiles_two_seconds_apart_and_visits_them_coarse_to_fine()
    {
        // 16 s at 30 fps, a tile every 2 s -> 8 tiles at frames 0, 60, ... 420
        var plan = Storyboard.Plan(frameCount: 480, framesPerSecond: 30, TimeSpan.FromSeconds(2), maxTiles: 240);

        Assert.Equal([0L, 240, 120, 360, 60, 180, 300, 420], plan);
    }

    [Fact]
    public void Plan_caps_long_videos_by_widening_the_spacing()
    {
        // ~23 min at 29.97 fps = ~690 tiles at 2 s. capped at 240, spread over the whole video
        var plan = Storyboard.Plan(41_186, 29.97, TimeSpan.FromSeconds(2), maxTiles: 240);

        Assert.Equal(240, plan.Count);
        Assert.Equal(plan.Count, plan.Distinct().Count());
        Assert.InRange(plan.Max(), 40_800, 41_185);
    }

    [Fact]
    public void Nearest_returns_the_closest_built_tile_from_either_side()
    {
        var storyboard = Storyboard.Empty.With(new(100, Pixel)).With(new(0, Pixel)).With(new(200, Pixel));

        Assert.Null(Storyboard.Empty.Nearest(5));
        Assert.Equal(0, storyboard.Nearest(40)!.FrameIndex);
        Assert.Equal(100, storyboard.Nearest(60)!.FrameIndex);
        Assert.Equal(200, storyboard.Nearest(9_999)!.FrameIndex);
        Assert.Equal(3, storyboard.Count);
    }

    [Fact]
    public async Task Loading_builds_tiles_in_the_background_and_hover_finds_them_without_decoding()
    {
        var decoders = new List<FakeVideoDecoder>();
        await using var workspace = new VideoWorkspace(
            new FakeVideoDecoderFactory(_ => { var d = new FakeVideoDecoder(frameCount: 300, fps: 30); lock (decoders) { decoders.Add(d); } return d; }),
            new RecordingFrameSink(), TimeProvider.System);
        var jobs = new ProcessingJobManager();
        var service = new FramePreviewService(workspace, new MarkerImaging(), TimeProvider.System, jobs: jobs, decoders: new FakeVideoDecoderFactory(_ => new FakeVideoDecoder(frameCount: 300, fps: 30)));
        await workspace.OpenAsync("video.mp4");

        service.LoadStoryboard();
        await Task.WhenAll(jobs.ActiveJobs.Select(j => j.Completion)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(5, service.Storyboard.Count); // 10 s video, a tile every 2 s
        var tile = service.NearestTile(130)!;
        Assert.Equal(120, tile.FrameIndex);
        Assert.Equal(120, tile.Image.Pixels[0]); // the tile really is that frame
    }

    [Fact]
    public void Pixel_bytes_add_up_the_tiles_and_follow_a_replaced_tile()
    {
        var small = new ImageBuffer(new byte[6], 2, 1, 6);
        var large = new ImageBuffer(new byte[12], 4, 1, 12);

        var storyboard = Storyboard.Empty.With(new(0, small)).With(new(60, large));
        Assert.Equal(18, storyboard.PixelBytes);

        storyboard = storyboard.With(new(0, large)); // same frame: replaces, does not add
        Assert.Equal(24, storyboard.PixelBytes);
        Assert.Equal(0, Storyboard.Empty.PixelBytes);
    }

    [Fact]
    public async Task Statistics_show_the_finished_build_and_its_decoder_and_frames_released()
    {
        await using var workspace = new VideoWorkspace(
            new FakeVideoDecoderFactory(_ => new FakeVideoDecoder(frameCount: 300, fps: 30)), new RecordingFrameSink(), TimeProvider.System);
        var jobs = new ProcessingJobManager();
        var service = new FramePreviewService(workspace, new MarkerImaging(), TimeProvider.System, jobs: jobs, decoders: new FakeVideoDecoderFactory(_ => new FakeVideoDecoder(frameCount: 300, fps: 30)));
        await workspace.OpenAsync("video.mp4");

        service.LoadStoryboard();
        await Task.WhenAll(jobs.ActiveJobs.Select(j => j.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        var stats = service.GetStatistics();

        Assert.Equal(5, stats.TilesBuilt);
        Assert.Equal(5, stats.TilesPlanned);
        Assert.Equal(5 * 3, stats.TileBytes); // the marker imaging makes 3-byte tiles
        Assert.False(stats.StoryboardBuilding);
        Assert.True(stats.StoryboardBuildTime > TimeSpan.Zero);
        Assert.False(stats.StoryboardDecoderOpen); // closed as soon as building ended
        Assert.Equal(0, stats.FramesInFlight);
        Assert.Equal(1, stats.PeakFramesInFlight); // one frame at a time
    }

    // stands in for EmguFrameImaging: keeps only the frame's marker byte
    private sealed class MarkerImaging : IFrameImaging
    {
        public ImageBuffer Resize(OwnedVideoFrame frame, NormalizedRect? region, int maxWidth, int maxHeight) =>
            new([frame.GetPixels()[0], 0, 0], 1, 1, 3);
    }
}
