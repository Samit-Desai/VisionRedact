using System.Drawing;
using Emgu.CV;
using VisionRedact.Application.Media;
using VisionRedact.Application.Tracking;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Infrastructure.Video;

// classic OpenCV trackers for hand-drawn regions: given a box on one frame, find the same thing on the next ones by
// its look, no AI
public enum VisualTrackerAlgorithm
{
    // fast, solid for rigid motion. the default
    Kcf = 0,

    // copes better w/ appearance change, but slower than KCF
    Mil,

    // usually the most accurate, but in Emgu CV 4.13.0.5924 (x64) Update fails on the first frame for every input
    // tried (ADR-0016). kept selectable to check again after an Emgu upgrade
    Csrt,
}

// RegionTrackingJob gets a fresh tracker per run (each keeps its own memory of the object) and disposes it.
// KCF won: CSRT failed in this Emgu build, MIL tracked as well but slower (ADR-0016)
public sealed class EmguVisualTrackerFactory(VisualTrackerAlgorithm algorithm = VisualTrackerAlgorithm.Kcf) : IVisualTrackerFactory
{
    // anything unknown -> KCF
    public IVisualTracker Create() => new EmguVisualTracker(algorithm switch
    {
        VisualTrackerAlgorithm.Mil => new TrackerMIL(),
        VisualTrackerAlgorithm.Csrt => new TrackerCSRT(),
        _ => new TrackerKCF(),
    });
}

// one OpenCV Tracker behind IVisualTracker: normalized boxes in and out, px for OpenCV. owns the native tracker,
// one job drives it
internal sealed class EmguVisualTracker(Tracker tracker) : IVisualTracker
{
    // from Initialize, to turn OpenCV's px boxes back into normalized ones
    private int _width;
    private int _height;

    // learns what the object looks like: the px inside the box on this frame
    public void Initialize(OwnedVideoFrame frame, NormalizedRect box)
    {
        _width = frame.Width;
        _height = frame.Height;

        var p = box.ToPixels(frame.Width, frame.Height);
        if (p.IsEmpty)
        {
            throw new ArgumentException("Cannot track an empty region.", nameof(box));
        }

        // zero-copy view, the tracker copies what it needs into its own state
        using var mat = FrameViews.Wrap(frame);
        tracker.Init(mat, new Rectangle(p.X, p.Y, p.Width, p.Height));
    }

    // false (and an empty box) once it lost the object
    public bool TryUpdate(OwnedVideoFrame frame, out NormalizedRect box)
    {
        using var mat = FrameViews.Wrap(frame);

        var found = tracker.Update(mat, out var rect);
        box = found ? NormalizedRect.FromPixels(rect.X, rect.Y, rect.Width, rect.Height, _width, _height) : NormalizedRect.Empty;
        return found;
    }

    public void Dispose() => tracker.Dispose();
}
