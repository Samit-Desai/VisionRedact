namespace VisionRedact.Application.Diagnostics;

// the last Capacity durations of a repeated step, as avg / p95 / max for the Diagnostics panel:
// the preview redaction time (PlaybackSession) and the WritePixels time (the presenter).
// a rolling window shows the current cost, e.g. switch preview redactions off and it follows within Capacity samples.
// one writer, any reader. a slot may be overwritten while it's read, but each read is one whole value
// (a Volatile 64-bit read), at worst a newer one
public sealed class RecentDurations
{
    public const int Capacity = 128;

    private readonly long[] _ticks = new long[Capacity];
    private long _count;

    // one thread at a time
    public void Add(TimeSpan duration)
    {
        // Summarize reads the count and the slots from another thread
        var count = Interlocked.Increment(ref _count);
        Volatile.Write(ref _ticks[(count - 1) % Capacity], duration.Ticks);
    }

    // Samples = 0 before the first
    public DurationSummary Summarize()
    {
        var count = (int)Math.Min(Interlocked.Read(ref _count), Capacity);
        if (count == 0)
        {
            return default;
        }

        // copies at most Capacity values and sorts them, cheap at the panel's 2 Hz
        var ticks = new long[count];
        for (var i = 0; i < count; i++)
        {
            ticks[i] = Volatile.Read(ref _ticks[i]);
        }

        Array.Sort(ticks);
        var p95 = ticks[(int)Math.Ceiling(count * 0.95) - 1];
        return new DurationSummary(count, TimeSpan.FromTicks((long)ticks.Average()), TimeSpan.FromTicks(p95), TimeSpan.FromTicks(ticks[^1]));
    }
}

public readonly record struct DurationSummary(int Samples, TimeSpan Average, TimeSpan P95, TimeSpan Max);
