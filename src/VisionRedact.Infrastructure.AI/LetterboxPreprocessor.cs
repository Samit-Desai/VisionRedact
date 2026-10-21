using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Infrastructure.AI;

// value = (px - Mean) * Scale, RGB planes, bars filled w/ PadValue.
// UltraFace: (x - 127) / 128, pad 127. YOLO: x / 255, pad 114 (gray), same as in its training
internal sealed record PreprocessSpec(int Width, int Height, float Mean, float Scale, byte PadValue);

// frame -> letterbox resize -> bars -> BGR to RGB planes (HWC -> CHW) -> floats. OnnxObjectDetector, once per scanned frame.
// letterbox, never stretch: a squashed face is harder to find. LetterboxTransform maps the boxes back (ADR-0012).
// every buffer is made once and reused. they used to be per frame: big floats on the LOH and a Gen2 GC every few
// frames (Performance.md, optimization 2). not thread-safe, the detector's lock serializes Prepare
internal sealed class LetterboxPreprocessor : IDisposable
{
    private readonly PreprocessSpec _spec;
    private readonly Mat _resized = new();
    private readonly Mat _canvas;

    public LetterboxPreprocessor(PreprocessSpec spec)
    {
        _spec = spec;
        _canvas = new Mat(spec.Height, spec.Width, DepthType.Cv8U, 3);
        Tensor = new float[3 * spec.Width * spec.Height];
        Shape = [1, 3, spec.Height, spec.Width];
    }

    // CHW floats, valid until the next Prepare
    public float[] Tensor { get; }

    // [1, 3, h, w]
    public long[] Shape { get; }

    // fills Tensor, returns model-input px -> source frame
    public LetterboxTransform Prepare(OwnedVideoFrame frame)
    {
        // letterbox in whole px: one scale, the scaled size, centering offsets
        var (w, h) = (_spec.Width, _spec.Height);
        var scale = Math.Min((double)w / frame.Width, (double)h / frame.Height);
        var scaledWidth = Math.Clamp((int)Math.Round(frame.Width * scale), 1, w);
        var scaledHeight = Math.Clamp((int)Math.Round(frame.Height * scale), 1, h);
        var offsetX = (w - scaledWidth) / 2;
        var offsetY = (h - scaledHeight) / 2;

        // Mat header over the frame's px (no copy). Stride, b/c rows may be padded
        using (var source = new Mat(frame.Height, frame.Width, DepthType.Cv8U, 3, frame.DataPointer, frame.Stride))
        {
            // Area when shrinking (1080p -> 640 is the usual case), it avoids aliasing on small faces
            CvInvoke.Resize(source, _resized, new Size(scaledWidth, scaledHeight), 0, 0, scale < 1 ? Inter.Area : Inter.Linear);
        }

        // paint the bars, then copy the frame into the centered window (roi is a view into the canvas)
        _canvas.SetTo(new MCvScalar(_spec.PadValue, _spec.PadValue, _spec.PadValue));
        using (var roi = new Mat(_canvas, new Rectangle(offsetX, offsetY, scaledWidth, scaledHeight)))
        {
            _resized.CopyTo(roi);
        }

        FillTensor();

        // Fit() uses exact offsets, ours are whole px. the gap stays under one model px
        return LetterboxTransform.Fit(new SizeD(frame.Width, frame.Height), new SizeD(w, h));
    }

    public void Dispose()
    {
        _resized.Dispose();
        _canvas.Dispose();
    }

    private unsafe void FillTensor()
    {
        var plane = _spec.Width * _spec.Height;
        var pixels = new ReadOnlySpan<byte>((void*)_canvas.DataPointer, plane * 3); // canvas is continuous (created, not a view)
        var tensor = Tensor.AsSpan();
        var red = tensor[..plane];
        var green = tensor.Slice(plane, plane);
        var blue = tensor.Slice(2 * plane, plane);
        var mean = _spec.Mean;
        var scale = _spec.Scale;

        for (var i = 0; i < plane; i++)
        {
            var p = i * 3; // OpenCV is B, G, R; the models want R, G, B planes
            blue[i] = (pixels[p] - mean) * scale;
            green[i] = (pixels[p + 1] - mean) * scale;
            red[i] = (pixels[p + 2] - mean) * scale;
        }
    }
}
