namespace VisionRedact.Application.Playback;

// the two playback tuning numbers. App.xaml.cs registers Default, VideoWorkspace passes it to PlaybackSession.Start
public sealed record PlaybackOptions
{
    public static PlaybackOptions Default { get; } = new();

    // size of the decode-ahead buffer: covers a slow frame (e.g. a keyframe) w/o much extra delay after a seek.
    // small on purpose, preview wants a quick seek, not throughput. memory = this * frame size
    public int DecodeAheadFrames { get; init; } = 3;

    // max frames left out in a row (skipped early by the decode task or dropped late by the present task).
    // then one is shown anyway and the clock restarts from it, so lateness can't pile up
    public int MaxConsecutiveLateDrops { get; init; } = 4;

    // fail before any task starts, e.g. on a 0-size buffer
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(DecodeAheadFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DecodeAheadFrames, 64);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxConsecutiveLateDrops);
    }
}
