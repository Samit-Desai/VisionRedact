using VisionRedact.Application.Diagnostics;

namespace VisionRedact.Application.Tests.Diagnostics;

public class RateMeterTests
{
    [Fact]
    public void A_2997_fps_stream_sampled_every_half_second_reads_close_to_2997()
    {
        // frames come at exactly 29.97 fps, the sampler wakes every 480 or 520 ms, uneven like a real UI timer
        var meter = new RateMeter(TimeSpan.FromSeconds(2));
        double? rate = null;
        var t = TimeSpan.Zero;
        for (var i = 0; i < 20; i++)
        {
            t += TimeSpan.FromMilliseconds(i % 2 == 0 ? 480 : 520);
            var frames = (long)Math.Floor(t.TotalSeconds * 29.97);
            rate = meter.Add(frames, t);
        }

        Assert.InRange(rate!.Value, 29.5, 30.5);
    }

    [Fact]
    public void The_first_sample_has_no_rate_yet()
    {
        Assert.Null(new RateMeter(TimeSpan.FromSeconds(2)).Add(100, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_counter_that_restarts_starts_a_new_window()
    {
        var meter = new RateMeter(TimeSpan.FromSeconds(2));
        meter.Add(0, TimeSpan.Zero);
        meter.Add(3_000, TimeSpan.FromSeconds(1));

        Assert.Null(meter.Add(10, TimeSpan.FromSeconds(2))); // new video: counter back near zero
        Assert.Equal(10, meter.Add(20, TimeSpan.FromSeconds(3))!.Value, 3);
    }
}
