using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Microsoft.ML.OnnxRuntime;
using VisionRedact.Application.Detections;
using VisionRedact.Infrastructure.AI;
using VisionRedact.Infrastructure.Video;
using Xunit.Abstractions;

namespace VisionRedact.IntegrationTests.AI;

// DirectML on every GPU that DXGI reports (ADR-0017).
// careful: w/o a GPU the two GPU tests loop over nothing and pass, only the CPU check really runs
public sealed class GpuInferenceTests(ITestOutputHelper output)
{
    private readonly IReadOnlyList<InferenceTarget> _targets = new DxgiAdapterCatalog().GetTargets();

    [Fact]
    public void Catalog_always_offers_the_cpu_first_and_lists_hardware_gpus()
    {
        foreach (var t in _targets)
        {
            output.WriteLine($"{t.Key,-6} {t}");
        }

        Assert.Equal(InferenceTarget.Cpu, _targets[0]);
        Assert.All(_targets.Skip(1), t => Assert.Equal(InferenceDevice.DirectML, t.Device));
        Assert.DoesNotContain(_targets, t => t.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)); // software adapter excluded
    }

    [RequiresModelFact("models/version-RFB-320.onnx", "testdata/input_0.pb", "testdata/output_0.pb", "testdata/output_1.pb")]
    public void Every_gpu_reproduces_the_model_zoo_reference_outputs()
    {
        var (dims, input) = TensorProtoReader.Read(ModelFiles.TestData("input_0.pb"));
        var (_, expectedScores) = TensorProtoReader.Read(ModelFiles.TestData("output_0.pb"));
        var (_, expectedBoxes) = TensorProtoReader.Read(ModelFiles.TestData("output_1.pb"));

        foreach (var gpu in _targets.Where(t => t.Device == InferenceDevice.DirectML))
        {
            using var options = OnnxDetectorFactory.CreateSessionOptions(0);
            options.EnableMemoryPattern = false;
            options.AppendExecutionProvider_DML(gpu.AdapterIndex);
            using var session = new InferenceSession(ModelFiles.UltraFace, options);
            using var value = OrtValue.CreateTensorValueFromMemory(input, dims);
            using var run = new RunOptions();
            using var outputs = session.Run(run, ["input"], [value], ["scores", "boxes"]);

            // looser than the CPU check: GPU kernels do the math differently (Performance.md, optimization 4)
            var scoreError = MaxAbs(outputs[0].GetTensorDataAsSpan<float>(), expectedScores);
            var boxError = MaxAbs(outputs[1].GetTensorDataAsSpan<float>(), expectedBoxes);
            output.WriteLine($"{gpu}: max |Δscore| = {scoreError:E2}, max |Δbox| = {boxError:E2}");
            Assert.True(scoreError < 2e-2 && boxError < 2e-2, $"{gpu}: outputs differ from the reference by {scoreError:E2} / {boxError:E2}.");
        }
    }

    [RequiresModelFact("models/version-RFB-320.onnx", "models/yolo11n-face.onnx")]
    public void Detectors_run_on_every_gpu_through_the_factory()
    {
        var factory = new OnnxDetectorFactory(ModelFiles.Options);
        using var mat = new Mat(1080, 1920, DepthType.Cv8U, 3);
        mat.SetTo(new MCvScalar(90, 90, 90));
        using var frame = new MatVideoFrame(mat.Clone(), 0, TimeSpan.Zero);

        foreach (var gpu in _targets.Where(t => t.Device == InferenceDevice.DirectML))
        {
            foreach (var id in new[] { OnnxModelCatalog.UltraFaceId, OnnxModelCatalog.YoloFaceId })
            {
                using var detector = factory.Create(ModelFiles.Model(id), gpu);
                output.WriteLine($"{id} on {gpu}: {detector.Info.ExecutionProvider}");
                Assert.StartsWith("DirectML", detector.Info.ExecutionProvider);
                Assert.Empty(detector.Detect(frame));
            }
        }
    }

    private static float MaxAbs(ReadOnlySpan<float> actual, float[] expected)
    {
        var max = 0f;
        for (var i = 0; i < expected.Length; i++)
        {
            max = Math.Max(max, Math.Abs(actual[i] - expected[i]));
        }

        return max;
    }
}
