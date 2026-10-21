using VisionRedact.Application.Media;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Application.Detections;

// a raw box from a detector, normalized to the source frame. no id, no frame, no review state yet
public readonly record struct DetectionCandidate(NormalizedRect Box, float Confidence, ObjectClass Class);

// facts about a loaded detector, for the job name, the log and Diagnostics (ExecutionProvider = CPU or GPU)
public sealed record DetectorInfo(string Name, string ModelFile, string Classes, int InputWidth, int InputHeight, string ExecutionProvider);

// finds objects on one frame. the scan only knows this port, so a plate or person model is a new model, not a new pipeline.
// sync on purpose: it's native CPU/GPU work, the caller picks the thread (the scan's AI task, or a Task.Run).
// must return clamped boxes above the confidence threshold w/o duplicates (NMS, or the model did it).
// the frame is only borrowed, the caller disposes it. cancel stops a running model where the runtime allows it
public interface IObjectDetector : IDisposable
{
    DetectorInfo Info { get; }

    IReadOnlyList<DetectionCandidate> Detect(OwnedVideoFrame frame, CancellationToken cancellationToken = default);
}

// OnnxDetectorFactory. separate from the detector b/c a load is slow and can fail in its own ways (DetectionException).
// GPU can't run the model -> it falls back to CPU and says so in DetectorInfo.ExecutionProvider instead of failing
public interface IObjectDetectorFactory
{
    IObjectDetector Create(DetectionModel model, InferenceTarget target);
}

// TODO: only a test reads Kind so far, the UI just shows the Message. ok for now
public enum DetectionErrorKind
{
    Unknown = 0,
    ModelNotFound,
    InvalidModel,
    RuntimeUnavailable,
    InferenceFailed,
}

// the one exception for expected detection failures (missing or bad model, no runtime, a failed run).
// Infrastructure.AI wraps ONNX Runtime errors in it, so nothing above has to know ONNX types
public sealed class DetectionException : Exception
{
    public DetectionException(DetectionErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public DetectionException()
        : this(DetectionErrorKind.Unknown, "A detection error occurred.")
    {
    }

    public DetectionException(string message)
        : this(DetectionErrorKind.Unknown, message)
    {
    }

    public DetectionException(string message, Exception innerException)
        : this(DetectionErrorKind.Unknown, message, innerException)
    {
    }

    public DetectionErrorKind Kind { get; }
}
