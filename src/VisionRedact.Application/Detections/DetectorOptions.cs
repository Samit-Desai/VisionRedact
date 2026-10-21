namespace VisionRedact.Application.Detections;

public enum InferenceDevice
{
    Cpu = 0,

    // any DirectX 12 GPU, no CUDA install. if the GPU can't make the session, the factory falls back to CPU and says so
    DirectML,
}

// settings for all detection models, registered once in App.xaml.cs. starting values, tune them on real footage
public sealed record DetectorOptions
{
    public static DetectorOptions Default { get; } = new();

    // relative = under the app folder. ArcFaceEmbedderFactory looks for the face models here too
    public string ModelsDirectory { get; init; } = "models";

    // when set, overrides every model's own default.
    // lower = fewer missed faces but more false boxes to review. for redaction a miss is the worse one
    public float? ConfidenceThreshold { get; init; }

    // NmsDecoder drops a box that overlaps a better box of the same class by more than this (IoU)
    public float NmsIouThreshold { get; init; } = 0.4f;

    // share of the frame. a box with either side smaller is dropped as noise
    public double MinimumBoxSize { get; init; } = 0.004;

    public int MaxDetectionsPerFrame { get; init; } = 300;

    // ONNX Runtime intra-op threads, 0 = the runtime picks. the ArcFace session uses it too
    public int IntraOpThreads { get; init; }

    public string ResolveModelsDirectory() =>
        Path.IsPathRooted(ModelsDirectory) ? ModelsDirectory : Path.Combine(AppContext.BaseDirectory, ModelsDirectory);

    public float ConfidenceFor(DetectionModel model) => ConfidenceThreshold ?? model.DefaultConfidence;
}
