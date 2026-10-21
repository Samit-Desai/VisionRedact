using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Redaction;

namespace VisionRedact.Application.Redaction;

// the one rule for what gets blurred on a frame. preview (RedactionPreviewProcessor) and export
// (RedactionSnapshot.PlanFor) both call it, so what you review is what you export (ADR-0015).
// pure: same inputs, same answer, any thread
public static class RedactionPlanner
{
    public static IReadOnlyList<RedactionInstruction> Plan(
        long frameIndex,
        int frameWidth,
        int frameHeight,
        IReadOnlyList<Detection> detections,
        IReadOnlyList<RedactionRegion> regions,
        RedactionSettings settings)
    {
        var instructions = new List<RedactionInstruction>(detections.Count + 2);

        // AI boxes: all but Rejected, so Pending is blurred too (ADR-0014). grown by the padding.
        // ToPixels rounds outward and clamps, so a junk box never reaches the pixel code
        foreach (var detection in detections)
        {
            if (!detection.IsRedacted)
            {
                continue;
            }

            var roi = detection.Box.Inflate(settings.Padding).ToPixels(frameWidth, frameHeight);
            if (!roi.IsEmpty)
            {
                instructions.Add(new RedactionInstruction(roi, settings.DetectionEffect));
            }
        }

        // drawn regions active on this frame. each keeps its own effect, not the project's detection effect
        foreach (var region in regions)
        {
            if (!region.IsActiveAt(frameIndex))
            {
                continue;
            }

            var roi = region.BoxAt(frameIndex).ToPixels(frameWidth, frameHeight); // follows the tracked Path if it has one
            if (!roi.IsEmpty)
            {
                instructions.Add(new RedactionInstruction(roi, region.Effect));
            }
        }

        return instructions;
    }
}
