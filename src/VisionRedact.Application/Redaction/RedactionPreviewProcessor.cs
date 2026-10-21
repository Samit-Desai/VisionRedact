using VisionRedact.Application.Detections;
using VisionRedact.Application.Media;
using VisionRedact.Application.Playback;

namespace VisionRedact.Application.Redaction;

// blurs each preview frame while you play, w/ the same planner and effects as the export.
// decode task -> playback redaction task (here) -> decode-ahead buffer -> screen.
// it used to run on the decode task right after decoding; on its own task the next decode overlaps it
// (docs/VideoPipeline.md). remember: buffered frames were blurred w/ the old state, so after an edit
// while paused the player calls PlaybackSession.Refresh
public sealed class RedactionPreviewProcessor : IPreviewFrameProcessor
{
    private readonly IDetectionRepository _detections;
    private readonly IRedactionRegionStore _regions;
    private readonly IRedactionProcessor _processor;
    // the "Preview redactions" switch: UI thread writes, the redaction task reads it every frame. one bool, so volatile
    // is enough. off = outlines only, the export still blurs
    private volatile bool _isEnabled = true;

    public RedactionPreviewProcessor(IDetectionRepository detections, IRedactionRegionStore regions, IRedactionProcessor processor)
    {
        _detections = detections;
        _regions = regions;
        _processor = processor;
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public void Process(OwnedVideoFrame frame)
    {
        if (!_isEnabled)
        {
            return;
        }

        // read once, so padding, effect and strength come from the same version even if a slider moves right now
        var settings = _regions.Settings;
        var plan = RedactionPlanner.Plan(
            frame.FrameIndex,
            frame.Width,
            frame.Height,
            _detections.GetForDisplay(frame.FrameIndex),
            _regions.Regions,
            settings);

        // most frames have nothing to hide
        if (plan.Count > 0)
        {
            _processor.Apply(frame, plan, settings.Strength);
        }
    }
}
