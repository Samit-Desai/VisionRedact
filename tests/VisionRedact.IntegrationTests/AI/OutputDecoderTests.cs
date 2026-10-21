using Microsoft.ML.OnnxRuntime;
using VisionRedact.Application.Detections;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;
using VisionRedact.Infrastructure.AI;

namespace VisionRedact.IntegrationTests.AI;

// the output decoders on hand-made tensors, no model file needed: each layout reads right, the filters apply,
// and NMS runs only where the format needs it (ADR-0021)
public sealed class OutputDecoderTests
{
    private const int Input = 640;
    private static readonly LetterboxTransform Identity = LetterboxTransform.Fit(new SizeD(Input, Input), new SizeD(Input, Input));
    private static readonly IReadOnlyDictionary<int, ObjectClass> FaceOnly = new Dictionary<int, ObjectClass> { [0] = ObjectClass.Face };

    [Fact]
    public void Nms_free_rows_become_candidates_in_source_coordinates()
    {
        float[] rows =
        [
            64, 64, 128, 192, 0.9f, 0, // face
            320, 320, 384, 384, 0.2f, 0, // below the threshold
            0, 0, 64, 64, 0.8f, 3, // class the model catalog does not map
        ];

        var candidates = Decode(new YoloEndToEndDecoder("out", rows: 3, maxDetections: 300, FaceOnly), rows, [1, 3, 6]);

        var face = Assert.Single(candidates);
        Assert.Equal(ObjectClass.Face, face.Class);
        Assert.Equal(0.9f, face.Confidence);
        AssertBox(new NormalizedRect(0.1, 0.1, 0.1, 0.2), face.Box);
    }

    [Fact]
    public void Nms_free_decoder_keeps_overlapping_boxes_because_the_model_already_resolved_them()
    {
        // two faces overlapping a lot (people hugging): an NMS-free head reports both on purpose
        float[] rows = [100, 100, 200, 200, 0.9f, 0, 110, 100, 210, 200, 0.85f, 0];

        var candidates = Decode(new YoloEndToEndDecoder("out", 2, 300, FaceOnly), rows, [1, 2, 6]);

        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void Nms_free_decoder_caps_the_count_keeping_the_most_confident()
    {
        float[] rows = [0, 0, 64, 64, 0.5f, 0, 100, 100, 164, 164, 0.9f, 0, 200, 200, 264, 264, 0.7f, 0];

        var candidates = Decode(new YoloEndToEndDecoder("out", 3, maxDetections: 2, FaceOnly), rows, [1, 3, 6]);

        Assert.Equal([0.9f, 0.7f], candidates.Select(c => c.Confidence));
    }

    [Fact]
    public void Nms_free_decoder_never_reads_past_a_short_tensor()
    {
        float[] rows = [0, 0, 64, 64, 0.9f, 0]; // one row, although the shape claimed 300

        var candidates = Decode(new YoloEndToEndDecoder("out", rows: 300, 300, FaceOnly), rows, [1, 1, 6]);

        Assert.Single(candidates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Classic_yolo_reads_center_size_boxes_in_both_layouts(bool anchorsLast)
    {
        // 2 anchors, 1 class: anchor 0 is a 64x128 face centered at (96, 128), anchor 1 scores too low
        float[][] anchors = [[96, 128, 64, 128, 0.9f], [300, 300, 50, 50, 0.1f]];
        var data = anchorsLast ? Transpose(anchors) : anchors.SelectMany(a => a).ToArray();
        long[] shape = anchorsLast ? [1, 5, 2] : [1, 2, 5];

        var candidates = Decode(new YoloDecoder("out", valuesPerAnchor: 5, anchors: 2, anchorsLast, FaceOnly), data, shape);

        var face = Assert.Single(candidates);
        AssertBox(new NormalizedRect(0.1, 0.1, 0.1, 0.2), face.Box);
    }

    [Fact]
    public void Classic_yolo_rejects_nan_scores()
    {
        float[] data = [10, 10, 5, 5, float.NaN]; // [1, 1, 5] transposed layout

        Assert.Empty(Decode(new YoloDecoder("out", 5, 1, anchorsLast: false, FaceOnly), data, [1, 1, 5]));
    }

    [Fact]
    public void Nms_decorator_removes_duplicates_from_a_classic_head()
    {
        // one face found by two neighboring anchors: a classic head needs NMS to keep only the best one
        float[][] anchors = [[150, 150, 100, 100, 0.9f], [152, 150, 100, 100, 0.8f]];
        var decoder = new NmsDecoder(new YoloDecoder("out", 5, 2, anchorsLast: false, FaceOnly), iouThreshold: 0.4f, maxDetections: 300);

        var candidates = Decode(decoder, anchors.SelectMany(a => a).ToArray(), [1, 2, 5]);

        Assert.Equal(0.9f, Assert.Single(candidates).Confidence);
    }

    [Fact]
    public void Ultraface_reads_normalized_corners_and_the_face_score()
    {
        float[] scores = [0.1f, 0.9f, 0.8f, 0.2f]; // anchor 0 = face, anchor 1 = background
        float[] boxes = [0.1f, 0.1f, 0.2f, 0.3f, 0.5f, 0.5f, 0.6f, 0.6f];
        using var scoresValue = OrtValue.CreateTensorValueFromMemory(scores, [1, 2, 2]);
        using var boxesValue = OrtValue.CreateTensorValueFromMemory(boxes, [1, 2, 4]);

        var candidates = new UltraFaceDecoder("scores", "boxes", Input, Input).Decode([scoresValue, boxesValue], Identity, 0.5f, 0.004);

        var face = Assert.Single(candidates);
        AssertBox(new NormalizedRect(0.1, 0.1, 0.1, 0.2), face.Box);
    }

    [Theory]
    [InlineData(640, 640, 8400, 8500)]
    [InlineData(320, 320, 2100, 2125)]
    [InlineData(640, 384, 5040, 5100)]
    public void Yolo_grid_size_matches_the_detection_heads(int width, int height, int p5, int p6)
    {
        Assert.Equal([p5, p6], YoloGrid.AnchorCounts(width, height));
    }

    [Theory]
    [InlineData("True", null, DetectionModelFormat.YoloEndToEnd)]
    [InlineData(null, "True", DetectionModelFormat.YoloEndToEnd)]
    [InlineData("False", "False", DetectionModelFormat.Yolo)]
    [InlineData(null, null, DetectionModelFormat.Yolo)]
    public void Ultralytics_export_metadata_decides_the_format(string? end2end, string? nms, DetectionModelFormat expected)
    {
        var metadata = new Dictionary<string, string> { ["task"] = "detect" };
        if (end2end is not null)
        {
            metadata["end2end"] = end2end;
        }

        if (nms is not null)
        {
            metadata["nms"] = nms;
        }

        Assert.Equal(expected, OnnxModelCatalog.UltralyticsFormat(metadata));
    }

    [Theory]
    [InlineData("[1280, 1280]", 1280, 1280)]
    [InlineData("[384, 640]", 640, 384)] // Ultralytics order is (height, width)
    [InlineData("640", 640, 640)]
    public void Trained_image_size_is_read_from_ultralytics_metadata(string imgsz, int width, int height)
    {
        Assert.Equal((width, height), OnnxDetectorFactory.ParseImageSize(imgsz));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[a, b]")]
    [InlineData("[0, 640]")]
    public void Unreadable_image_size_falls_back_to_the_default(string imgsz)
    {
        Assert.Null(OnnxDetectorFactory.ParseImageSize(imgsz));
    }

    private static IReadOnlyList<DetectionCandidate> Decode(IOutputDecoder decoder, float[] data, long[] shape)
    {
        using var value = OrtValue.CreateTensorValueFromMemory(data, shape);
        return decoder.Decode([value], Identity, confidenceThreshold: 0.5f, minimumBoxSize: 0.004);
    }

    private static float[] Transpose(float[][] anchors)
    {
        var channels = anchors[0].Length;
        var result = new float[anchors.Length * channels];
        for (var a = 0; a < anchors.Length; a++)
        {
            for (var c = 0; c < channels; c++)
            {
                result[(c * anchors.Length) + a] = anchors[a][c];
            }
        }

        return result;
    }

    private static void AssertBox(NormalizedRect expected, NormalizedRect actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Width, actual.Width, 4);
        Assert.Equal(expected.Height, actual.Height, 4);
    }
}
