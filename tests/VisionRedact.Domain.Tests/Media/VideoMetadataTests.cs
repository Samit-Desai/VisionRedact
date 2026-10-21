using VisionRedact.Domain.Media;

namespace VisionRedact.Domain.Tests.Media;

public class VideoMetadataTests
{
    private static VideoMetadata Create(double fps = 30, long frames = 300) =>
        new("C:\\videos\\sample.mp4", 1920, 1080, fps, frames, 1_000_000, "h264");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Rejects_unusable_frame_rates(double fps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(fps: fps));
    }

    [Fact]
    public void Rejects_non_positive_dimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoMetadata("a.mp4", 0, 1080, 30, 10, 0, "h264"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoMetadata("a.mp4", 1920, -1, 30, 10, 0, "h264"));
    }

    [Fact]
    public void Duration_is_frame_count_divided_by_fps()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), Create(fps: 30, frames: 300).Duration);
    }

    [Theory]
    [InlineData(23.976)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(29.97)]
    [InlineData(30000.0 / 1001.0)]
    [InlineData(30)]
    [InlineData(59.94)]
    [InlineData(60)]
    public void TimeOfFrame_and_FrameAtTime_round_trip_for_common_rates(double fps)
    {
        var metadata = Create(fps: fps, frames: 200_000); // an hour or more of video, so rounding error has time to add up

        foreach (var frame in new long[] { 0, 1, 2, 29, 30, 1_001, 99_999, 199_999 })
        {
            Assert.Equal(frame, metadata.FrameAtTime(metadata.TimeOfFrame(frame)));
        }
    }

    [Fact]
    public void FrameAtTime_clamps_to_valid_range()
    {
        var metadata = Create(fps: 30, frames: 300);

        Assert.Equal(0, metadata.FrameAtTime(TimeSpan.FromSeconds(-5)));
        Assert.Equal(299, metadata.FrameAtTime(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void FrameAtTime_returns_the_frame_being_displayed_mid_interval()
    {
        var metadata = Create(fps: 25, frames: 100);

        // frame 3 is on screen from 120 ms to 160 ms
        Assert.Equal(3, metadata.FrameAtTime(TimeSpan.FromMilliseconds(159)));
        Assert.Equal(4, metadata.FrameAtTime(TimeSpan.FromMilliseconds(160)));
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(150, 150)]
    [InlineData(299, 299)]
    [InlineData(300, 299)]
    [InlineData(long.MaxValue, 299)]
    public void ClampFrame_limits_index_to_video(long input, long expected)
    {
        Assert.Equal(expected, Create(frames: 300).ClampFrame(input));
    }

    [Fact]
    public void Empty_video_has_no_valid_frames_but_clamps_safely()
    {
        var metadata = Create(frames: 0);

        Assert.Equal(-1, metadata.LastFrameIndex);
        Assert.Equal(0, metadata.ClampFrame(42));
        Assert.False(metadata.ContainsFrame(0));
        Assert.Equal(TimeSpan.Zero, metadata.Duration);
    }
}
