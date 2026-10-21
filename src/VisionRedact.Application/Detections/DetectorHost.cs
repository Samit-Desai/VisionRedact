using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Media;

namespace VisionRedact.Application.Detections;

// keeps the loaded models: one ONNX session per (model, device), loaded on first use, kept until shutdown.
// a session load is slow native work (+ GPU upload), never per frame or per job.
// AnalysisService / FrameDetectionService -> GetAsync -> a handle (NonOwningDetector or CompositeDetector)
public sealed class DetectorHost : IDisposable
{
    private readonly IObjectDetectorFactory _factory;
    private readonly ILogger _logger;
    // SemaphoreSlim, not lock: we await the load while holding it, and lock can't contain an await.
    // also stops two callers loading the same model twice
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IObjectDetector> _loaded = [];

    // set by DetectionPreferences on the UI thread, read in GetAsync on whatever thread
    private volatile InferenceTarget _target = InferenceTarget.Cpu;
    private bool _disposed;

    public DetectorHost(IObjectDetectorFactory factory, ILogger<DetectorHost>? logger = null)
    {
        _factory = factory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    // only for detectors asked for from now on. a running job keeps its device
    public InferenceTarget Target
    {
        get => _target;
        set => _target = value ?? InferenceTarget.Cpu;
    }

    // careful: hands out the shared detector itself, never dispose it. app code uses the list overload, which wraps it.
    // throws DetectionException when the model can't be loaded
    public async Task<IObjectDetector> GetAsync(DetectionModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // read once, so the key and the load use the same device even if the setting changes right now
            var target = _target;
            var key = model.Id + "@" + target.Key;
            if (_loaded.TryGetValue(key, out var existing))
            {
                return existing;
            }

            // a failed load throws before _loaded is set: not cached, the next call just tries again

            // blocking native work, so off the caller's thread
            var detector = await Task.Run(() => _factory.Create(model, target), cancellationToken).ConfigureAwait(false);
            _loaded[key] = detector;
            _logger.LogInformation("Detector loaded: {Name} ({Model}, {Width}x{Height}, classes {Classes}, {Provider}).",
                detector.Info.Name, detector.Info.ModelFile, detector.Info.InputWidth, detector.Info.InputHeight, detector.Info.Classes, detector.Info.ExecutionProvider);
            return detector;
        }
        finally
        {
            _gate.Release();
        }
    }

    // several models (e.g. faces + persons) -> one detector that runs them all on each frame
    public async Task<IObjectDetector> GetAsync(IReadOnlyList<DetectionModel> models, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0)
        {
            throw new ArgumentException("Select at least one detection model.", nameof(models));
        }

        var detectors = new List<IObjectDetector>(models.Count);
        foreach (var model in models)
        {
            detectors.Add(await GetAsync(model, cancellationToken).ConfigureAwait(false));
        }

        return detectors.Count == 1 ? new NonOwningDetector(detectors[0]) : new CompositeDetector(detectors);
    }

    // remember: ShellViewModel.ShutdownAsync cancels and awaits all jobs first, then calls this.
    // that's why there's no _gate here
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var detector in _loaded.Values)
        {
            detector.Dispose();
        }

        _loaded.Clear();
    }

    // what jobs get. Dispose does nothing, so a finished job can't unload the shared model
    private sealed class NonOwningDetector(IObjectDetector inner) : IObjectDetector
    {
        public DetectorInfo Info => inner.Info;

        public IReadOnlyList<DetectionCandidate> Detect(OwnedVideoFrame frame, CancellationToken cancellationToken = default) =>
            inner.Detect(frame, cancellationToken);

        public void Dispose()
        {
            // empty on purpose, the host owns the real one
        }
    }

    // runs each model on the frame and just joins the lists. no NMS across models, so two models can both report
    // one face. that ends up as two tracks, which LinkConstraints treats as a duplicate, not as two people
    private sealed class CompositeDetector(IReadOnlyList<IObjectDetector> detectors) : IObjectDetector
    {
        public DetectorInfo Info { get; } = new(
            string.Join(" + ", detectors.Select(d => d.Info.Name)),
            string.Join(", ", detectors.Select(d => d.Info.ModelFile)),
            string.Join(", ", detectors.Select(d => d.Info.Classes)),
            detectors.Max(d => d.Info.InputWidth),
            detectors.Max(d => d.Info.InputHeight),
            string.Join(", ", detectors.Select(d => d.Info.ExecutionProvider).Distinct()));

        public IReadOnlyList<DetectionCandidate> Detect(OwnedVideoFrame frame, CancellationToken cancellationToken = default)
        {
            var all = new List<DetectionCandidate>();
            foreach (var detector in detectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                all.AddRange(detector.Detect(frame, cancellationToken));
            }

            return all;
        }

        public void Dispose()
        {
            // the host owns the detectors inside
        }
    }
}
