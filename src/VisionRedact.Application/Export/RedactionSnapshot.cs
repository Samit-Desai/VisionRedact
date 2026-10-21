using VisionRedact.Application.Detections;
using VisionRedact.Application.Redaction;
using VisionRedact.Domain.Redaction;

namespace VisionRedact.Application.Export;

// frozen copy of everything that decides the blur, taken when Export is pressed (ADR-0019).
// an export runs for minutes while you keep reviewing, or even open another video (that resets the live stores).
// the file still matches the review at the moment you pressed Export.
// ShellViewModel: Capture -> coverage warning -> ExportJob. then PlanFor, frame by frame, on the export's redact task
public sealed class RedactionSnapshot
{
    private readonly InMemoryDetectionRepository _detections;
    private readonly Lazy<RedactionCoverage> _coverage;

    private RedactionSnapshot(InMemoryDetectionRepository detections, IReadOnlyList<RedactionRegion> regions, RedactionSettings settings)
    {
        _detections = detections;
        Regions = regions;
        Settings = settings;
        _coverage = new Lazy<RedactionCoverage>(() => detections.GetCoverage(), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<RedactionRegion> Regions { get; }

    public RedactionSettings Settings { get; }

    public int DetectionCount => _detections.Count;

    // how much of the video the face scan covered at capture time. the other frames get no automatic face blur
    // (drawn regions still apply), so the shell asks before exporting, and the done message says it again
    public RedactionCoverage Coverage => _coverage.Value;

    public static RedactionSnapshot Capture(long frameCount, IDetectionRepository detections, IRedactionRegionStore regions)
    {
        // a private InMemoryDetectionRepository, so later review edits don't reach this export, and GetForDisplay
        // holds and slides boxes exactly like the preview. nothing else uses this copy, so its lock is never contended
        var copy = new InMemoryDetectionRepository();
        copy.Load(frameCount, detections.Snapshot(), detections.AnalyzedFrames(), detections.HoldFrames);
        return new RedactionSnapshot(copy, regions.Regions.ToArray(), regions.Settings);
    }

    // nothing to blur, a plain re-encode. for tests
    public static RedactionSnapshot Empty(long frameCount)
    {
        var none = new InMemoryDetectionRepository();
        none.Reset(frameCount);
        return new RedactionSnapshot(none, [], RedactionSettings.Default);
    }

    // same planner and same GetForDisplay as the preview: what you reviewed is what you export
    public IReadOnlyList<RedactionInstruction> PlanFor(long frameIndex, int width, int height) =>
        RedactionPlanner.Plan(frameIndex, width, height, _detections.GetForDisplay(frameIndex), Regions, Settings);
}
