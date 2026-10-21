namespace VisionRedact.Application.Playback;

// "when is frame n due?" for the present task: anchor time + (n - anchor frame) / fps.
// why not a Task.Delay per frame: every wait ends a bit late and that adds up, the video drifts slower and slower.
// from one anchor a single frame can be late, but the next one is measured from the anchor again.
// TimeProvider = the real monotonic clock in the app, FakeTimeProvider in the tests. not thread-safe, present task only
public sealed class PlaybackClock
{
    private readonly TimeProvider _timeProvider;
    private readonly double _framesPerSecond;
    private long _anchorTimestamp;

    public PlaybackClock(TimeProvider timeProvider, double framesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        _timeProvider = timeProvider;
        _framesPerSecond = framesPerSecond;
        FrameDuration = TimeSpan.FromSeconds(1.0 / framesPerSecond);
    }

    public bool IsRunning { get; private set; }

    public long AnchorFrame { get; private set; }

    // TimeProvider timestamp when AnchorFrame was due. PlaybackSession hands it to the decode task (PacingAnchor)
    public long AnchorTimestamp => _anchorTimestamp;

    public TimeSpan FrameDuration { get; }

    // 0 while stopped
    public TimeSpan Elapsed => IsRunning ? _timeProvider.GetElapsedTime(_anchorTimestamp) : TimeSpan.Zero;

    // anchorFrame is due now. also re-anchors a running clock
    public void Start(long anchorFrame)
    {
        AnchorFrame = anchorFrame;
        _anchorTimestamp = _timeProvider.GetTimestamp();
        IsRunning = true;
    }

    // pause, seek, step. the next Start picks a new anchor
    public void Stop() => IsRunning = false;

    public TimeSpan DueOffset(long frameIndex) => TimeSpan.FromSeconds((frameIndex - AnchorFrame) / _framesPerSecond);

    /// <summary>Positive: wait this long. Negative: already overdue.</summary>
    public TimeSpan TimeUntilDue(long frameIndex) => DueOffset(frameIndex) - Elapsed;

    // late = its whole display slot has passed. preview may drop it, export never uses this
    public bool IsLate(long frameIndex) => IsRunning && TimeUntilDue(frameIndex) < -FrameDuration;
}
