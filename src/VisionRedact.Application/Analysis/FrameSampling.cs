namespace VisionRedact.Application.Analysis;

// which frames the AI runs on. a fixed choice, never "whatever the PC manages" (ADR-0013).
// AnalysisJob asks ShouldAnalyze per frame. HoldFrames goes to the store, which reuses a scanned frame's boxes on the
// frames after it and slides them toward the next scan.
// fewer scans = faster, but a face seen only between two scans can be missed
public interface IFrameSamplingStrategy
{
    string Description { get; }

    // sampling interval - 1
    int HoldFrames { get; }

    bool ShouldAnalyze(long frameIndex);
}

// slowest, best coverage, nothing to hold
public sealed class EveryFrameSampling : IFrameSamplingStrategy
{
    public string Description => "Every frame";

    public int HoldFrames => 0;

    public bool ShouldAnalyze(long frameIndex) => true;
}

// frames 0, N, 2N... each result covers the N - 1 frames after it
public sealed class EveryNthFrameSampling : IFrameSamplingStrategy
{
    public EveryNthFrameSampling(int interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, 1);
        Interval = interval;
    }

    public int Interval { get; }

    public string Description => Interval == 1 ? "Every frame" : $"1 of every {Interval} frames";

    public int HoldFrames => Interval - 1;

    public bool ShouldAnalyze(long frameIndex) => frameIndex % Interval == 0;
}

// about targetFps frames per second of video, whatever the source rate (5 a second of 59.94 fps = about every 12th frame).
// the default choice in DetectionPreferences is 5 a second
public sealed class TargetRateSampling : IFrameSamplingStrategy
{
    private readonly double _step;

    public TargetRateSampling(double targetFps, double sourceFps)
    {
        if (!(targetFps > 0) || !(sourceFps > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(targetFps), "Frame rates must be positive.");
        }

        // can't scan faster than the video. step = source frames per scanned frame
        TargetFps = Math.Min(targetFps, sourceFps);
        _step = sourceFps / TargetFps;
    }

    public double TargetFps { get; }

    public string Description => $"{TargetFps:0.#} frames per second";

    // rounded up, so a step like 11.99 still covers the longest gap between two scans
    public int HoldFrames => (int)Math.Ceiling(_step) - 1;

    // from the frame index alone, no running counter, so rounding errors can't add up.
    // frame i is chosen when floor(i / step) changes = the first frame at or after each multiple of the step
    public bool ShouldAnalyze(long frameIndex) =>
        frameIndex == 0 || Math.Floor(frameIndex / _step) != Math.Floor((frameIndex - 1) / _step);
}
