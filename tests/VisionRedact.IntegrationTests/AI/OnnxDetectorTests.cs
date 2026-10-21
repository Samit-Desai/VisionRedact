using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Microsoft.ML.OnnxRuntime;
using VisionRedact.Application.Detections;
using VisionRedact.Domain.Detections;
using VisionRedact.Infrastructure.AI;
using VisionRedact.Infrastructure.Video;

namespace VisionRedact.IntegrationTests.AI;

// real ONNX Runtime: the zoo's reference outputs, preprocessing, and each load error mapped to its kind
public sealed class OnnxDetectorTests
{
    private const string UltraFaceFile = "models/version-RFB-320.onnx";
    private const string YoloFile = "models/yolo11n-face.onnx";
    private const string Yolo26File = "models/yolo26l-face.onnx";

    [RequiresModelFact(UltraFaceFile, "testdata/input_0.pb", "testdata/output_0.pb", "testdata/output_1.pb")]
    public void UltraFace_session_reproduces_the_model_zoo_reference_outputs()
    {
        // the zoo's input tensor through our session options must give the zoo's outputs.
        // that input is a numeric fixture, not a photo, so it has no confident faces
        var (inputDims, input) = TensorProtoReader.Read(ModelFiles.TestData("input_0.pb"));
        var (_, expectedScores) = TensorProtoReader.Read(ModelFiles.TestData("output_0.pb"));
        var (_, expectedBoxes) = TensorProtoReader.Read(ModelFiles.TestData("output_1.pb"));

        using var options = OnnxDetectorFactory.CreateSessionOptions(0);
        using var session = new InferenceSession(ModelFiles.UltraFace, options);
        using var value = OrtValue.CreateTensorValueFromMemory(input, inputDims);
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, ["input"], [value], ["scores", "boxes"]);

        Assert.True(MaxAbsDifference(outputs[0].GetTensorDataAsSpan<float>(), expectedScores) < 1e-3);
        Assert.True(MaxAbsDifference(outputs[1].GetTensorDataAsSpan<float>(), expectedBoxes) < 1e-3);
    }

    [Fact]
    public void Preprocessing_letterboxes_normalizes_and_orders_channels_as_the_model_expects()
    {
        // 640x360 frame (16:9) into a 320x240 input: content 320x180, 30-row bars above and below
        using var mat = new Mat(360, 640, DepthType.Cv8U, 3);
        mat.SetTo(new MCvScalar(10, 20, 250)); // B, G, R: strongly red
        using var frame = new MatVideoFrame(mat.Clone(), 0, TimeSpan.Zero);
        using var pre = new LetterboxPreprocessor(new PreprocessSpec(320, 240, Mean: 127f, Scale: 1f / 128f, PadValue: 127));

        var transform = pre.Prepare(frame);

        var plane = 320 * 240;
        float At(int channel, int x, int y) => pre.Tensor[(channel * plane) + (y * 320) + x];
        Assert.Equal(30, transform.OffsetY, 3);
        Assert.Equal(0f, At(0, 160, 5), 3);                          // padding normalizes to 0
        Assert.Equal((250 - 127) / 128f, At(0, 160, 120), 2);       // plane 0 is RED
        Assert.Equal((20 - 127) / 128f, At(1, 160, 120), 2);        // plane 1 is green
        Assert.Equal((10 - 127) / 128f, At(2, 160, 120), 2);        // plane 2 is blue
        Assert.Equal([1L, 3, 240, 320], pre.Shape);
    }

    [RequiresModelFact(UltraFaceFile, YoloFile)]
    public void Both_models_run_real_inference_and_find_nothing_on_a_blank_frame()
    {
        foreach (var id in new[] { OnnxModelCatalog.UltraFaceId, OnnxModelCatalog.YoloFaceId })
        {
            using var detector = new OnnxDetectorFactory(ModelFiles.Options).Create(ModelFiles.Model(id), InferenceTarget.Cpu);
            using var mat = new Mat(1080, 1920, DepthType.Cv8U, 3);
            mat.SetTo(new MCvScalar(90, 90, 90));
            using var frame = new MatVideoFrame(mat, 0, TimeSpan.Zero);

            Assert.Empty(detector.Detect(frame));
        }
    }

    [RequiresModelFact(Yolo26File)]
    public void Yolo26_runs_nms_free_at_the_size_it_was_trained_at()
    {
        // the file has a dynamic input. its "imgsz" metadata says it was trained at 1280x1280
        using var detector = new OnnxDetectorFactory(ModelFiles.Options).Create(ModelFiles.Model(OnnxModelCatalog.Yolo26FaceId), InferenceTarget.Cpu);
        using var mat = new Mat(1080, 1920, DepthType.Cv8U, 3);
        mat.SetTo(new MCvScalar(90, 90, 90));
        using var frame = new MatVideoFrame(mat, 0, TimeSpan.Zero);

        Assert.Equal((1280, 1280), (detector.Info.InputWidth, detector.Info.InputHeight));
        Assert.Empty(detector.Detect(frame));
    }

    [RequiresModelFact(UltraFaceFile, YoloFile)]
    public void Detector_info_reflects_each_model_contract()
    {
        using var ultra = new OnnxDetectorFactory(ModelFiles.Options).Create(ModelFiles.Model(OnnxModelCatalog.UltraFaceId), InferenceTarget.Cpu);
        using var yolo = new OnnxDetectorFactory(ModelFiles.Options).Create(ModelFiles.Model(OnnxModelCatalog.YoloFaceId), InferenceTarget.Cpu);

        Assert.Equal((320, 240), (ultra.Info.InputWidth, ultra.Info.InputHeight));
        Assert.Equal((640, 640), (yolo.Info.InputWidth, yolo.Info.InputHeight));
        Assert.Equal("CPU", yolo.Info.ExecutionProvider);
    }

    [Fact]
    public void Optional_local_face_image_is_detected_by_both_models()
    {
        // runs only w/ VR_FACE_IMAGE set to a photo with a clear face, and both models present.
        // TODO: otherwise it returns early and passes silently - use a skip attribute
        var path = Environment.GetEnvironmentVariable("VR_FACE_IMAGE");
        if (path is null || !File.Exists(path) || !File.Exists(ModelFiles.UltraFace) || !File.Exists(ModelFiles.YoloFace))
        {
            return;
        }

        foreach (var id in new[] { OnnxModelCatalog.UltraFaceId, OnnxModelCatalog.YoloFaceId })
        {
            using var detector = new OnnxDetectorFactory(ModelFiles.Options).Create(ModelFiles.Model(id), InferenceTarget.Cpu);
            using var frame = new MatVideoFrame(CvInvoke.Imread(path, ImreadModes.ColorBgr), 0, TimeSpan.Zero);
            Assert.NotEmpty(detector.Detect(frame));
        }
    }

    [RequiresModelFact(UltraFaceFile)]
    public void Cancelled_token_stops_before_inference()
    {
        using var detector = new OnnxDetectorFactory(ModelFiles.Options).Create(ModelFiles.Model(OnnxModelCatalog.UltraFaceId), InferenceTarget.Cpu);
        using var mat = new Mat(240, 320, DepthType.Cv8U, 3);
        using var frame = new MatVideoFrame(mat, 0, TimeSpan.Zero);

        Assert.ThrowsAny<OperationCanceledException>(() => detector.Detect(frame, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Missing_model_file_is_reported_as_ModelNotFound()
    {
        var model = FakeModel(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx"));

        var error = Assert.Throws<DetectionException>(() => new OnnxDetectorFactory(DetectorOptions.Default).Create(model, InferenceTarget.Cpu));

        Assert.Equal(DetectionErrorKind.ModelNotFound, error.Kind);
    }

    [Fact]
    public void Corrupt_model_file_is_reported_as_InvalidModel()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
        try
        {
            var error = Assert.Throws<DetectionException>(() => new OnnxDetectorFactory(DetectorOptions.Default).Create(FakeModel(path), InferenceTarget.Cpu));
            Assert.Equal(DetectionErrorKind.InvalidModel, error.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [RequiresModelFact(UltraFaceFile)]
    public void Model_with_the_wrong_format_fails_contract_validation()
    {
        // UltraFace declared as YOLO: outputs of the wrong shape must be rejected, not misread
        var model = FakeModel(ModelFiles.UltraFace) with { Classes = new Dictionary<int, ObjectClass> { [3] = ObjectClass.Face } };

        var error = Assert.Throws<DetectionException>(() => new OnnxDetectorFactory(DetectorOptions.Default).Create(model, InferenceTarget.Cpu));

        Assert.Equal(DetectionErrorKind.InvalidModel, error.Kind);
    }

    [RequiresModelFact(UltraFaceFile, YoloFile)]
    public void Classic_models_declared_as_nms_free_are_rejected_not_misparsed()
    {
        // neither real model has [1, K, 6] rows, so declaring them NMS-free must fail at load time
        foreach (var path in new[] { ModelFiles.UltraFace, ModelFiles.YoloFace })
        {
            var model = FakeModel(path) with { Format = DetectionModelFormat.YoloEndToEnd };

            var error = Assert.Throws<DetectionException>(() => new OnnxDetectorFactory(DetectorOptions.Default).Create(model, InferenceTarget.Cpu));

            Assert.Equal(DetectionErrorKind.InvalidModel, error.Kind);
        }
    }

    [RequiresModelFact(UltraFaceFile, YoloFile)]
    public void Catalog_lists_both_built_in_models_as_available()
    {
        var models = new OnnxModelCatalog(ModelFiles.Options).GetModels();

        Assert.Contains(models, m => m.Id == OnnxModelCatalog.UltraFaceId && m.IsAvailable && m.Format == DetectionModelFormat.UltraFace);
        Assert.Contains(models, m => m.Id == OnnxModelCatalog.YoloFaceId && m.IsAvailable && m.Format == DetectionModelFormat.Yolo);
        Assert.Contains(models, m => m.Id == OnnxModelCatalog.Yolo26FaceId && m.Format == DetectionModelFormat.YoloEndToEnd);
        Assert.Single(models, m => m.FilePath.EndsWith("yolo26l-face.onnx", StringComparison.OrdinalIgnoreCase)); // not also listed as "custom"
    }

    [Theory]
    [InlineData("{0: 'face'}", "0:Face")]
    [InlineData("{0: 'person', 1: 'bicycle', 2: 'car', 5: 'bus', 7: 'truck'}", "0:Person,2:Vehicle,5:Vehicle,7:Vehicle")]
    [InlineData("{0: 'license_plate'}", "0:LicensePlate")]
    [InlineData("{0: 'cup', 1: 'dog'}", "")]
    public void Ultralytics_class_names_map_to_domain_classes(string names, string expected)
    {
        var map = OnnxModelCatalog.ParseClassNames(names);

        Assert.Equal(expected, string.Join(",", map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));
    }

    private static float MaxAbsDifference(ReadOnlySpan<float> actual, float[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        var max = 0f;
        for (var i = 0; i < expected.Length; i++)
        {
            max = Math.Max(max, Math.Abs(actual[i] - expected[i]));
        }

        return max;
    }

    private static DetectionModel FakeModel(string path) =>
        new("test", "test", path, DetectionModelFormat.Yolo, new Dictionary<int, ObjectClass> { [0] = ObjectClass.Face }, 0.5f, string.Empty, true);
}
