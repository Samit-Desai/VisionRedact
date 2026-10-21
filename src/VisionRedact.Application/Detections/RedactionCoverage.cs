namespace VisionRedact.Application.Detections;

// inclusive on both ends
public readonly record struct FrameRange(long First, long Last)
{
    public long Length => Last - First + 1;
}

// how much of the video the face scan covers, for the warning in the Export dialog.
// blurring Pending boxes (ADR-0014) only helps faces the AI found. a frame no scan reached (never run, canceled, too far
// from a scanned frame) has no boxes at all, so its faces export unblurred unless a manual region covers them.
// covered = the GetForDisplay rule, not "every face was found". Gaps = the first few, GapCount = all of them
public sealed record RedactionCoverage(long TotalFrames, long CoveredFrames, IReadOnlyList<FrameRange> Gaps, long GapCount,
    long ScannedFrames)
{
    // also true for an empty video
    public bool IsComplete => CoveredFrames >= TotalFrames;

    // "Every frame" sampling: the AI ran on each frame itself, nothing held in between
    public bool IsEveryFrameScanned => ScannedFrames >= TotalFrames;

    // has frames, none scanned
    public bool IsNone => TotalFrames > 0 && CoveredFrames == 0;

    public long UncoveredFrames => TotalFrames - CoveredFrames;

    // 0..1, 1 for an empty video
    public double Fraction => TotalFrames == 0 ? 1.0 : (double)CoveredFrames / TotalFrames;
}
