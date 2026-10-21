using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using VisionRedact.Application.Media;
using VisionRedact.Application.Redaction;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.Redaction;

namespace VisionRedact.Infrastructure.Video;

// zero-copy Mat views over a frame's px: writing through the Mat changes the frame itself.
// used by the effects below, the tracker, the OpenCV writer and EmguFrameImaging
internal static class FrameViews
{
    // the Mat doesn't own this memory, disposing it frees only the header. the frame must outlive the view
    public static Mat Wrap(OwnedVideoFrame frame) =>
        new(frame.Height, frame.Width, DepthType.Cv8U, 3, frame.DataPointer, frame.Stride);

    public static Rectangle ToRectangle(PixelRect roi) => new(roi.X, roi.Y, roi.Width, roi.Height);
}

// kernel sized to the region, so a small face and a big face get the same relative blur.
// RedactionProcessor (export + live preview) picks the effect by Kind and calls Apply, in place.
// the first version blurred big regions at full size, which was most of the export time. now they're shrunk, blurred
// and stretched back: much cheaper, still a strong low-pass (Performance.md, optimization 3).
// TODO: not bit-identical to the old blur, and only the direct path has a "detail destroyed" test. ok for now
public sealed class GaussianBlurEffect : IRedactionEffect
{
    // short side <= this: blur directly
    private const int DirectPathLimit = 64;

    // bigger: shrink so the short side is this, blur, scale back up
    private const int ReducedSize = 48;

    public RedactionEffectKind Kind => RedactionEffectKind.GaussianBlur;

    /// <summary>Blurs roi in place. strength 0 (light) .. 1 (strong).</summary>
    public void Apply(OwnedVideoFrame frame, PixelRect roi, double strength)
    {
        // the region is a sub-Mat over the same memory, so writing "view" writes the frame
        using var mat = FrameViews.Wrap(frame);
        using var view = new Mat(mat, FrameViews.ToRectangle(roi));

        // strength -> passes (2 from 0.5 up, a wider blur) and kernel = 25..75% of the short side
        var passes = strength >= 0.5 ? 2 : 1;
        var shortSide = Math.Min(roi.Width, roi.Height);
        var relativeKernel = 0.25 + (0.5 * Math.Clamp(strength, 0, 1));

        if (shortSide <= DirectPathLimit)
        {
            BlurInPlace(view, Odd(Math.Max(3, (int)(shortSide * relativeKernel))), passes);
            return;
        }

        // shrink (Area = average the merged px), blur w/ a kernel for the small size, stretch back (Linear = smooth)
        var scale = (double)ReducedSize / shortSide;
        var small = new Size(Math.Max(1, (int)Math.Round(roi.Width * scale)), Math.Max(1, (int)Math.Round(roi.Height * scale)));
        using var reduced = new Mat();
        CvInvoke.Resize(view, reduced, small, 0, 0, Inter.Area);
        BlurInPlace(reduced, Odd(Math.Max(3, (int)(ReducedSize * relativeKernel))), passes);
        CvInvoke.Resize(reduced, view, new Size(roi.Width, roi.Height), 0, 0, Inter.Linear);
    }

    internal static void DirectBlur(OwnedVideoFrame frame, PixelRect roi, double strength)
    {
        // the old full-size blur, kept for the benchmark comparison
        using var mat = FrameViews.Wrap(frame);
        using var view = new Mat(mat, FrameViews.ToRectangle(roi));
        var kernel = Odd(Math.Max(3, (int)(Math.Min(roi.Width, roi.Height) * (0.25 + (0.5 * Math.Clamp(strength, 0, 1))))));
        BlurInPlace(view, kernel, strength >= 0.5 ? 2 : 1);
    }

    private static void BlurInPlace(Mat image, int kernel, int passes)
    {
        // src == dst, so it blurs in place. sigma 0 = OpenCV derives it from the kernel size
        for (var i = 0; i < passes; i++)
        {
            CvInvoke.GaussianBlur(image, image, new Size(kernel, kernel), 0);
        }
    }

    // GaussianBlur needs an odd kernel (it has a center px)
    private static int Odd(int value) => value % 2 == 0 ? value + 1 : value;
}

// mosaic: shrink w/ area averaging (each tiny px = one block's average color), grow back w/ Nearest (hard edges)
public sealed class PixelateEffect : IRedactionEffect
{
    public RedactionEffectKind Kind => RedactionEffectKind.Pixelate;

    public void Apply(OwnedVideoFrame frame, PixelRect roi, double strength)
    {
        using var mat = FrameViews.Wrap(frame);
        using var view = new Mat(mat, FrameViews.ToRectangle(roi));

        // block = 8..25% of the short side by strength, min 2 px, so any face ends up only a few blocks across
        var block = Math.Max(2, (int)(Math.Min(roi.Width, roi.Height) * (0.08 + (0.17 * Math.Clamp(strength, 0, 1)))));
        var small = new Size(Math.Max(1, roi.Width / block), Math.Max(1, roi.Height / block));
        using var reduced = new Mat();
        CvInvoke.Resize(view, reduced, small, 0, 0, Inter.Area);
        CvInvoke.Resize(reduced, view, new Size(roi.Width, roi.Height), 0, 0, Inter.Nearest);
    }
}

// black box: the only effect that keeps nothing of the original. strength is ignored
public sealed class SolidBlackEffect : IRedactionEffect
{
    public RedactionEffectKind Kind => RedactionEffectKind.SolidBlack;

    public void Apply(OwnedVideoFrame frame, PixelRect roi, double strength)
    {
        using var mat = FrameViews.Wrap(frame);
        using var view = new Mat(mat, FrameViews.ToRectangle(roi));

        view.SetTo(new MCvScalar(0, 0, 0));
    }
}
