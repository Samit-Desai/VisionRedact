using Microsoft.ML.OnnxRuntime;
using VisionRedact.Application.Detections;

namespace VisionRedact.Infrastructure.AI;

internal sealed record DecoderBinding(PreprocessSpec Preprocess, IOutputDecoder Decoder);

// one per model family: checks the outputs, picks the preprocessing and builds the decoder (w/ NMS or not).
// OnnxDetectorFactory loads every model the same way and leaves the rest to the profile (ADR-0021).
// came w/ YOLO26: its NMS-free [1, 300, 6] output passed the old check as "300 anchors, 2 classes" and decoded into
// junk boxes w/o any error. now the shape is checked exactly, once, at load
internal interface IModelFormatProfile
{
    DetectionModelFormat Format { get; }

    DecoderBinding Bind(InferenceSession session, DetectionModel model, int inputWidth, int inputHeight);
}

// UltraFace RFB-320: scores [1, N, 2] + boxes [1, N, 4], input (x - 127) / 128
internal sealed class UltraFaceProfile(DetectorOptions options) : IModelFormatProfile
{
    public DetectionModelFormat Format => DetectionModelFormat.UltraFace;

    public DecoderBinding Bind(InferenceSession session, DetectionModel model, int inputWidth, int inputHeight)
    {
        // find them by the last dim (2 = background/face, 4 = corners), exports name them differently
        string? scores = null, boxes = null;
        foreach (var (name, meta) in session.OutputMetadata)
        {
            var last = meta.Dimensions.Length > 0 ? meta.Dimensions[^1] : 0;
            scores = last == 2 ? name : scores;
            boxes = last == 4 ? name : boxes;
        }

        if (scores is null || boxes is null)
        {
            throw ModelContract.Violation(model, "expected 'scores' [1, N, 2] and 'boxes' [1, N, 4] outputs");
        }

        var decoder = new UltraFaceDecoder(scores, boxes, inputWidth, inputHeight);
        return new DecoderBinding(
            new PreprocessSpec(inputWidth, inputHeight, Mean: 127f, Scale: 1f / 128f, PadValue: 127),
            new NmsDecoder(decoder, options.NmsIouThreshold, options.MaxDetectionsPerFrame));
    }
}

// YOLO v8/v11 detect head: one output [1, 4 + C, N] or transposed [1, N, 4 + C], N = the anchor grid.
// input 0..1, gray 114 bars
internal sealed class YoloProfile(DetectorOptions options) : IModelFormatProfile
{
    public DetectionModelFormat Format => DetectionModelFormat.Yolo;

    public DecoderBinding Bind(InferenceSession session, DetectionModel model, int inputWidth, int inputHeight)
    {
        var (name, dims) = ModelContract.SingleStaticOutput(session, model, "[1, 4 + classes, anchors]");

        // the anchor dim must be the stride grid of this input size (8400 at 640x640). that's what tells a classic
        // head from an NMS-free [1, 300, 6], which would fit the shape otherwise
        var expected = YoloGrid.AnchorCounts(inputWidth, inputHeight);
        var anchorsLast = expected.Contains(dims[2]);
        if (!anchorsLast && !expected.Contains(dims[1]))
        {
            throw ModelContract.Violation(model, $"output [{string.Join(", ", dims)}] has no anchor dimension of {string.Join(" or ", expected)} for a {inputWidth}×{inputHeight} input — is it an NMS-free export (YoloEndToEnd)?");
        }

        var anchors = anchorsLast ? dims[2] : dims[1];
        var valuesPerAnchor = anchorsLast ? dims[1] : dims[2];

        // every class id in the catalog must exist in the output, else labels come from the wrong column
        if (valuesPerAnchor < 5 || model.Classes.Keys.Any(k => k >= valuesPerAnchor - 4))
        {
            throw ModelContract.Violation(model, $"output has {valuesPerAnchor - 4} classes, which does not match the model's class names");
        }

        var decoder = new YoloDecoder(name, valuesPerAnchor, anchors, anchorsLast, model.Classes);
        return new DecoderBinding(YoloPreprocessing.For(inputWidth, inputHeight), new NmsDecoder(decoder, options.NmsIouThreshold, options.MaxDetectionsPerFrame));
    }
}

// NMS-free YOLO (YOLO26, YOLOv10): one output [1, K, 6], same preprocessing as YOLO, no NMS
internal sealed class YoloEndToEndProfile(DetectorOptions options) : IModelFormatProfile
{
    public DetectionModelFormat Format => DetectionModelFormat.YoloEndToEnd;

    public DecoderBinding Bind(InferenceSession session, DetectionModel model, int inputWidth, int inputHeight)
    {
        var (name, dims) = ModelContract.SingleStaticOutput(session, model, "[1, detections, 6]");
        if (dims[2] != 6)
        {
            throw ModelContract.Violation(model, $"expected rows of 6 values (x1, y1, x2, y2, confidence, class), got [{string.Join(", ", dims)}]");
        }

        var decoder = new YoloEndToEndDecoder(name, dims[1], options.MaxDetectionsPerFrame, model.Classes);
        return new DecoderBinding(YoloPreprocessing.For(inputWidth, inputHeight), decoder);
    }
}

// as in YOLO training: RGB 0..1, gray 114 bars
internal static class YoloPreprocessing
{
    public static PreprocessSpec For(int width, int height) => new(width, height, Mean: 0f, Scale: 1f / 255f, PadValue: 114);
}

// one anchor per grid cell per stride. P3-P5 heads = strides 8, 16, 32, P6 adds 64.
// 640x640: 80*80 + 40*40 + 20*20 = 8400, + 10*10 = 8500 w/ P6
internal static class YoloGrid
{
    public static IReadOnlyList<int> AnchorCounts(int width, int height)
    {
        var p5 = Cells(width, height, 8) + Cells(width, height, 16) + Cells(width, height, 32);
        return [p5, p5 + Cells(width, height, 64)];
    }

    private static int Cells(int width, int height, int stride) => (width / stride) * (height / stride);
}

// output checks the profiles share, and the one error they all throw
internal static class ModelContract
{
    // the first output, 3-D w/ fixed sizes [?, a, b]. the batch dim isn't checked
    public static (string Name, int[] Dims) SingleStaticOutput(InferenceSession session, DetectionModel model, string expectedShape)
    {
        if (session.OutputMetadata.Count < 1)
        {
            throw Violation(model, $"expected an output shaped {expectedShape}");
        }

        var (name, meta) = session.OutputMetadata.First();
        var dims = meta.Dimensions;
        if (dims.Length != 3 || dims[1] <= 0 || dims[2] <= 0)
        {
            throw Violation(model, $"expected a static output {expectedShape}, got [{string.Join(", ", dims)}] — re-export with a fixed image size");
        }

        return (name, dims);
    }

    public static DetectionException Violation(DetectionModel model, string detail) =>
        new(DetectionErrorKind.InvalidModel, $"'{model.DisplayName}' does not match the {model.Format} model contract: {detail}.");
}
