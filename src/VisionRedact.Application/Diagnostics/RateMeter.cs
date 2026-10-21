namespace VisionRedact.Application.Diagnostics;

// events per second from a running total, averaged over a sliding window (2 s in the Diagnostics panel).
// NOTE: the fps rows used to count frames over one half-second sample. whole frames over an uneven tick made a
// correct rate look jumpy, so now they average over a window: same math, less noise.
// feed it the total + a monotonic timestamp each sample. a total that goes down (new video, new job) restarts it.
// not thread-safe, one sampler
public sealed class RateMeter
{
    private readonly TimeSpan _window;
    private readonly Queue<(TimeSpan At, long Total)> _samples = new();

    public RateMeter(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _window = window;
    }

    // the rate over the window, or null until two samples span some time
    public double? Add(long total, TimeSpan at)
    {
        if (_samples.Count > 0 && total < _samples.Last().Total)
        {
            _samples.Clear(); // the counter restarted
        }

        _samples.Enqueue((at, total));
        // keep the oldest sample that's still at least one window back, so the rate always spans ~the full window
        while (_samples.Count > 2 && at - ElementAt(1).At >= _window)
        {
            _samples.Dequeue();
        }

        var (firstAt, firstTotal) = _samples.Peek();
        var seconds = (at - firstAt).TotalSeconds;
        return seconds > 0 ? (total - firstTotal) / seconds : null;
    }

    public void Reset() => _samples.Clear();

    private (TimeSpan At, long Total) ElementAt(int index) => _samples.ElementAt(index);
}
