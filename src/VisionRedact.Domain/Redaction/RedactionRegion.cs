using VisionRedact.Domain.Geometry;

namespace VisionRedact.Domain.Redaction;

public enum RedactionEffectKind
{
    GaussianBlur = 0,
    Pixelate,
    SolidBlack,
}

public readonly record struct TrackPoint(long Frame, NormalizedRect Box);

// hand-drawn box, blurred from StartFrame to EndFrame (inclusive). for what the AI missed: a name badge, a screen,
// a face turned away. a human drew it, so no review state.
// drawn on the player -> RedactionRegionStore -> RegionTrackingJob may add a Path -> BoxAt, preview + export
public sealed record RedactionRegion(
    Guid Id,
    NormalizedRect Box,
    long StartFrame,
    long EndFrame,
    RedactionEffectKind Effect,
    string Label = "",
    IReadOnlyList<TrackPoint>? Path = null)
{
    public bool IsActiveAt(long frameIndex) => frameIndex >= StartFrame && frameIndex <= EndFrame;

    public long FrameCount => EndFrame - StartFrame + 1;

    public bool IsTracked => Path is { Count: > 0 };

    // tracked: the latest Path point at or before the frame (Path is sorted by frame). else the drawn box
    public NormalizedRect BoxAt(long frameIndex)
    {
        if (Path is not { Count: > 0 } path || frameIndex < path[0].Frame)
        {
            return Box;
        }

        // last point with Frame <= frameIndex. mid rounds up, otherwise lo = mid would loop forever
        int lo = 0, hi = path.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (path[mid].Frame <= frameIndex)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return path[lo].Box;
    }

    public static RedactionRegion Create(NormalizedRect box, long startFrame, long endFrame, RedactionEffectKind effect, string label = "")
    {
        if (endFrame < startFrame)
        {
            (startFrame, endFrame) = (endFrame, startFrame);
        }

        return new RedactionRegion(Guid.NewGuid(), box, Math.Max(0, startFrame), endFrame, effect, label);
    }

    // records compare lists by reference -> compare the path item by item
    public bool Equals(RedactionRegion? other) =>
        other is not null && Id == other.Id && Box == other.Box && StartFrame == other.StartFrame && EndFrame == other.EndFrame
        && Effect == other.Effect && Label == other.Label
        && (Path ?? []).SequenceEqual(other.Path ?? []);

    // path length only. cheap, and equal regions still hash the same
    public override int GetHashCode() => HashCode.Combine(Id, Box, StartFrame, EndFrame, Effect, Label, Path?.Count ?? 0);
}

// blur settings for the whole project, saved with it. set in the Redaction tab, read by the planner and the effect
// (same for preview and export).
// Padding = share of the box added on each side, b/c model boxes hug the face and hair/ears/motion blur stick out.
// Strength = 0..1, blur radius or pixel block size relative to the region
public sealed record RedactionSettings(RedactionEffectKind DetectionEffect = RedactionEffectKind.GaussianBlur, double Padding = 0.2, double Strength = 0.8)
{
    public static RedactionSettings Default { get; } = new();
}
