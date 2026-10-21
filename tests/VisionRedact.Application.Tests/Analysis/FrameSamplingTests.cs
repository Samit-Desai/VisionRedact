using VisionRedact.Application.Analysis;

namespace VisionRedact.Application.Tests.Analysis;

public class FrameSamplingTests
{
    [Fact]
    public void Every_frame_samples_everything_and_holds_nothing()
    {
        var s = new EveryFrameSampling();

        Assert.All(Enumerable.Range(0, 100), i => Assert.True(s.ShouldAnalyze(i)));
        Assert.Equal(0, s.HoldFrames);
    }

    [Theory]
    [InlineData(2, 50)]
    [InlineData(5, 20)]
    [InlineData(7, 15)]
    public void Every_nth_frame_samples_one_in_n_and_holds_the_gap(int n, int expectedIn100)
    {
        var s = new EveryNthFrameSampling(n);

        Assert.Equal(expectedIn100, Enumerable.Range(0, 100).Count(i => s.ShouldAnalyze(i)));
        Assert.True(s.ShouldAnalyze(0));
        Assert.Equal(n - 1, s.HoldFrames);
    }

    [Theory]
    [InlineData(5, 59.94, 300)]   // AVCHD 59.94 fps, 5 a second for 60 s
    [InlineData(10, 30, 600)]
    [InlineData(30, 25, 1500)]    // target above source rate: capped to every frame
    public void Target_rate_samples_the_requested_rate_without_drift(double target, double source, int expected)
    {
        var s = new TargetRateSampling(target, source);
        var frames = (int)Math.Round(source * 60);

        var sampled = Enumerable.Range(0, frames).Count(i => s.ShouldAnalyze(i));

        Assert.InRange(sampled, expected - 1, expected + 1);
    }

    [Fact]
    public void Target_rate_hold_covers_the_gap_between_samples()
    {
        var s = new TargetRateSampling(5, 59.94); // step ~11.99 frames

        var sampled = Enumerable.Range(0, 1000).Where(i => s.ShouldAnalyze(i)).ToArray();
        var maxGap = sampled.Zip(sampled.Skip(1), (a, b) => b - a).Max();

        Assert.True(s.HoldFrames >= maxGap - 1, $"Hold {s.HoldFrames} does not cover gap {maxGap}.");
    }

    [Fact]
    public void Invalid_parameters_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EveryNthFrameSampling(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetRateSampling(0, 30));
    }
}
