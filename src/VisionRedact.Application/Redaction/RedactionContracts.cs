using VisionRedact.Application.Media;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.Redaction;

namespace VisionRedact.Application.Redaction;

// RedactionPlanner -> instructions -> RedactionProcessor -> one IRedactionEffect per kind.
// the pixel code itself is in Infrastructure.Video

// Roi in real px, already clamped to the frame and never empty (the planner makes these)
public readonly record struct RedactionInstruction(PixelRect Roi, RedactionEffectKind Effect);

// blur / pixelate / black box, one class each (ADR-0015). the Application only decides where and which
public interface IRedactionEffect
{
    RedactionEffectKind Kind { get; }

    /// <summary>Changes only the pixels inside roi, in place. The caller owns the frame.</summary>
    void Apply(OwnedVideoFrame frame, PixelRect roi, double strength);
}

public interface IRedactionProcessor
{
    void Apply(OwnedVideoFrame frame, IReadOnlyList<RedactionInstruction> instructions, double strength);
}

// one shared instance: the preview (RedactionPreviewProcessor) and the export's redact stage both use it.
// nothing changes after the ctor, so the two threads can share it
public sealed class RedactionProcessor : IRedactionProcessor
{
    private readonly Dictionary<RedactionEffectKind, IRedactionEffect> _effects;

    public RedactionProcessor(IEnumerable<IRedactionEffect> effects)
    {
        // don't forget: App.xaml.cs lists the effects by hand. a new kind missing there throws on first use
        _effects = effects.ToDictionary(e => e.Kind);
    }

    public void Apply(OwnedVideoFrame frame, IReadOnlyList<RedactionInstruction> instructions, double strength)
    {
        foreach (var instruction in instructions)
        {
            // the planner clamps already. again here, this is the last stop before native pixel writes
            var roi = instruction.Roi.ClampTo(frame.Width, frame.Height);
            if (roi.IsEmpty)
            {
                continue;
            }

            // missing effect = setup bug, so throw: the export fails instead of writing a face unblurred.
            // the preview catches it in PlaybackSession, logs it and plays on
            if (!_effects.TryGetValue(instruction.Effect, out var effect))
            {
                throw new InvalidOperationException($"No redaction effect registered for {instruction.Effect}.");
            }

            effect.Apply(frame, roi, strength);
        }
    }
}
