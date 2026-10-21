using Microsoft.ML.OnnxRuntime;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Media;

namespace VisionRedact.Infrastructure.AI;

// frame -> LetterboxPreprocessor -> session.Run -> decoder (in an NmsDecoder unless the head is NMS-free) -> candidates
// AnalysisJob calls Detect once per scanned frame, via a DetectorHost handle.
// one session for its whole life, made once by OnnxDetectorFactory. per frame only the input OrtValue (it wraps the
// reused tensor, no copy), a RunOptions and the outputs, all disposed before Detect returns
public sealed class OnnxObjectDetector : IObjectDetector
{
    private readonly InferenceSession _session;
    private readonly LetterboxPreprocessor _preprocessor;
    private readonly IOutputDecoder _decoder;
    private readonly DetectorOptions _options;
    private readonly float _confidenceThreshold;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    // Run itself is thread-safe, the reused buffers aren't. this lock is the price of reusing them (Performance.md,
    // optimization 2), parallel work comes from ORT's intra-op threads instead.
    // it also covers _disposed, so Dispose can't free native memory under a running Detect
    private readonly object _gate = new();
    private bool _disposed;

    internal OnnxObjectDetector(InferenceSession session, DetectorInfo info, string inputName, PreprocessSpec spec, IOutputDecoder decoder, DetectorOptions options, float confidenceThreshold)
    {
        _session = session;
        _decoder = decoder;
        _options = options;
        _confidenceThreshold = confidenceThreshold;
        Info = info;
        _inputNames = [inputName];
        _outputNames = [.. decoder.OutputNames];
        _preprocessor = new LetterboxPreprocessor(spec);
    }

    public DetectorInfo Info { get; }

    public IReadOnlyList<DetectionCandidate> Detect(OwnedVideoFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            // fills the reused tensor; keep the transform to map the boxes back
            var transform = _preprocessor.Prepare(frame);

            // cancel -> RunOptions.Terminate, ORT then stops between graph nodes.
            // static lambda w/ state: no closure alloc per frame
            using var input = OrtValue.CreateTensorValueFromMemory(_preprocessor.Tensor, _preprocessor.Shape);
            using var runOptions = new RunOptions();
            using var registration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);

            // an ORT error after our own Terminate is just a cancel. any other names the model and the frame
            IDisposableReadOnlyCollection<OrtValue> outputs;
            try
            {
                outputs = _session.Run(runOptions, _inputNames, [input], _outputNames);
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Inference was terminated by cancellation.", cancellationToken);
            }
            catch (OnnxRuntimeException ex)
            {
                throw new DetectionException(DetectionErrorKind.InferenceFailed, $"{Info.Name} failed on frame {frame.FrameIndex}.", ex);
            }

            // confidence + min size filter in the decoder. NMS or not is its business too (ADR-0021)
            using (outputs)
            {
                // the decoders read spans over the native outputs, only valid until `outputs` is disposed.
                // copying YOLO's output to an array would be an LOH allocation every frame (Performance.md, optimization 2)
                return _decoder.Decode(outputs, transform, _confidenceThreshold, _options.MinimumBoxSize);
            }
        }
    }

    // safe to call twice. the lock makes it wait for a running Detect
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.Dispose();
            _preprocessor.Dispose();
        }
    }
}
