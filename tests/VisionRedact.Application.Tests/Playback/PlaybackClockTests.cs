using Microsoft.Extensions.Time.Testing;
using VisionRedact.Application.Playback;

namespace VisionRedact.Application.Tests.Playback;

// FakeTimeProvider only moves when the test says so: timing tests w/o any real waiting
public class PlaybackClockTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Due_times_are_computed_from_the_anchor_not_accumulated()
    {
        var clock = new PlaybackClock(_time, framesPerSecond: 25); // 40 ms per frame
        clock.Start(anchorFrame: 100);

        Assert.Equal(TimeSpan.Zero, clock.TimeUntilDue(100));
        Assert.Equal(TimeSpan.FromMilliseconds(400), clock.TimeUntilDue(110));

        // a late wake-up: 1 s passes in one step, and frame 125 is due exactly now.
        // a Task.Delay per frame would have drifted by the lateness instead
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, clock.TimeUntilDue(125));
        Assert.Equal(TimeSpan.FromMilliseconds(40), clock.TimeUntilDue(126));
    }

    [Fact]
    public void A_frame_is_late_only_after_its_whole_display_slot_has_passed()
    {
        var clock = new PlaybackClock(_time, framesPerSecond: 25);
        clock.Start(anchorFrame: 0);

        _time.Advance(TimeSpan.FromMilliseconds(79)); // frame 0's slot ended at 40 ms; frame 1's slot is 40-80 ms
        Assert.True(clock.IsLate(0));
        Assert.False(clock.IsLate(1));

        _time.Advance(TimeSpan.FromMilliseconds(2)); // 81 ms
        Assert.True(clock.IsLate(1));
        Assert.False(clock.IsLate(2));
    }

    [Fact]
    public void Stopped_clock_never_reports_late_frames()
    {
        var clock = new PlaybackClock(_time, framesPerSecond: 30);
        clock.Start(0);
        clock.Stop();
        _time.Advance(TimeSpan.FromHours(1));

        Assert.False(clock.IsLate(0));
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Fact]
    public void Restart_re_anchors_after_a_seek()
    {
        var clock = new PlaybackClock(_time, framesPerSecond: 30);
        clock.Start(0);
        _time.Advance(TimeSpan.FromSeconds(10));

        clock.Start(anchorFrame: 5_000);

        Assert.Equal(5_000, clock.AnchorFrame);
        Assert.Equal(TimeSpan.Zero, clock.TimeUntilDue(5_000));
        Assert.False(clock.IsLate(5_000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(double.NaN)]
    public void Rejects_invalid_frame_rates(double fps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackClock(_time, fps));
    }
}
