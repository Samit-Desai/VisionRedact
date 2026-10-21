using VisionRedact.Application.Detections;
using VisionRedact.Infrastructure.AI;

namespace VisionRedact.IntegrationTests.AI;

// where the downloaded models and the zoo's reference data end up (the csproj copies them next to the tests)
internal static class ModelFiles
{
    public static string ModelsDirectory => Path.Combine(AppContext.BaseDirectory, "models");

    public static string UltraFace => Path.Combine(ModelsDirectory, "version-RFB-320.onnx");

    public static string YoloFace => Path.Combine(ModelsDirectory, "yolo11n-face.onnx");

    public static string TestData(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", name);

    public static DetectorOptions Options => new() { ModelsDirectory = ModelsDirectory };

    public static DetectionModel Model(string id) => new OnnxModelCatalog(Options).GetModels().Single(m => m.Id == id);
}

// skips instead of failing when a model or the test data is missing, e.g. on a fresh clone
public sealed class RequiresModelFactAttribute : FactAttribute
{
    public RequiresModelFactAttribute(params string[] relativeFiles)
    {
        var missing = relativeFiles.Where(f => !File.Exists(Path.Combine(AppContext.BaseDirectory, f))).ToArray();
        if (missing.Length > 0)
        {
            Skip = $"Missing {string.Join(", ", missing)} — run scripts/download-model.ps1.";
        }
    }
}
