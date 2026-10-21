using VisionRedact.Application.Diagnostics;

namespace VisionRedact.Application.Tests.Diagnostics;

public class RecentDurationsTests
{
    [Fact]
    public void Nothing_measured_yet_has_no_samples()
    {
        Assert.Equal(0, new RecentDurations().Summarize().Samples);
    }

    [Fact]
    public void Summary_gives_average_95th_percentile_and_slowest()
    {
        var durations = new RecentDurations();
        for (var ms = 1; ms <= 10; ms++)
        {
            durations.Add(TimeSpan.FromMilliseconds(ms));
        }

        var summary = durations.Summarize();

        Assert.Equal(10, summary.Samples);
        Assert.Equal(TimeSpan.FromMilliseconds(5.5), summary.Average);
        Assert.Equal(TimeSpan.FromMilliseconds(10), summary.P95); // under 20 samples, the 95th percentile is the slowest
        Assert.Equal(TimeSpan.FromMilliseconds(10), summary.Max);
    }

    [Fact]
    public void Only_the_most_recent_durations_count()
    {
        // 200 durations of 1..200 ms: only the last 128 (73..200 ms) are kept.
        var durations = new RecentDurations();
        for (var ms = 1; ms <= 200; ms++)
        {
            durations.Add(TimeSpan.FromMilliseconds(ms));
        }

        var summary = durations.Summarize();

        Assert.Equal(RecentDurations.Capacity, summary.Samples);
        Assert.Equal(TimeSpan.FromMilliseconds(136.5), summary.Average);
        Assert.Equal(TimeSpan.FromMilliseconds(194), summary.P95); // 122nd of 128 sorted values
        Assert.Equal(TimeSpan.FromMilliseconds(200), summary.Max);
    }
}
