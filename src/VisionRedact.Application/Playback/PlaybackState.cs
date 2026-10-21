using VisionRedact.Application.Diagnostics;

namespace VisionRedact.Application.Playback;

// only the present task sets it (an int in the session, swapped w/ Interlocked.Exchange).
// PlayerViewModel reads it on attach, then follows StateChanged (e.g. to flip Play/Pause)
public enum PlaybackState
{
    // first frame not on screen yet
    Starting = 0,
    Paused,
    Playing,

    // at the real end of the stream. Play starts again from frame 0
    Ended,

    // a task hit an error it can't recover from (decode, redaction stage, sink). the session is done
    Faulted,
}

// raised on the present task, so UI handlers post to the Dispatcher themselves
public sealed class PlaybackStateChangedEventArgs(PlaybackState previous, PlaybackState current, Exception? error) : EventArgs
{
    public PlaybackState Previous { get; } = previous;

    public PlaybackState Current { get; } = current;

    // set when Current is Faulted
    public Exception? Error { get; } = error;
}

// snapshot for the Diagnostics panel.
// DroppedLate = every frame left out for being late; SkippedEarly = the part the decode task skipped before the
// color conversion and the blur, so those saved work. DiscardedBySeek = made stale by a seek.
// AverageDecodeTime is ReadNext only. the preview processor is PreviewProcessTime, over its last
// RecentDurations.Capacity frames so it shows the current cost (no samples w/o a processor)
public readonly record struct PlaybackStatistics(
    long FramesDecoded,
    long FramesPresented,
    long FramesDroppedLate,
    long FramesSkippedEarly,
    long FramesDiscardedBySeek,
    TimeSpan AverageDecodeTime,
    int DecodeAheadCount,
    int DecodeAheadCapacity,
    DurationSummary PreviewProcessTime);
