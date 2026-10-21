using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.Tracking;
using VisionRedact.Domain.Jobs;

namespace VisionRedact.Application.Analysis;

// "Analyze video", one scan at a time.
// DetectionsViewModel -> StartAsync -> DetectorHost.GetAsync -> new AnalysisJob + new tracker -> ProcessingJobManager.Start
public sealed class AnalysisService
{
    private readonly IVideoDecoderFactory _decoderFactory;
    private readonly DetectorHost _detectorHost;
    private readonly IDetectionRepository _repository;
    private readonly IProcessingJobManager _jobs;
    private readonly Func<IObjectTracker>? _trackerFactory;
    private readonly AnalysisOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    // StartAsync may set it on a pool thread (after its await), the UI reads it. one reference, so volatile is enough
    private volatile AnalysisJob? _current;

    public AnalysisService(
        IVideoDecoderFactory decoderFactory,
        DetectorHost detectorHost,
        IDetectionRepository repository,
        IProcessingJobManager jobs,
        Func<IObjectTracker>? trackerFactory = null,
        AnalysisOptions? options = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        _decoderFactory = decoderFactory;
        _detectorHost = detectorHost;
        _repository = repository;
        _jobs = jobs;
        _trackerFactory = trackerFactory;
        _options = options ?? AnalysisOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    // the latest job, running or finished
    public AnalysisJob? Current => _current;

    public bool IsRunning => _current is { } job && !JobStateMachine.IsTerminal(job.State);

    // throws DetectionException when a selected model can't be loaded
    public async Task<AnalysisJob> StartAsync(string videoPath, IReadOnlyList<DetectionModel> models, IFrameSamplingStrategy sampling, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (IsRunning)
        {
            throw new InvalidOperationException("An analysis is already running. Cancel it first.");
        }

        // the first time this loads the model, after that it's the cached session
        var detector = await _detectorHost.GetAsync(models, cancellationToken).ConfigureAwait(false);
        // a new tracker per scan = no old tracks to match. ids still go on after HighestTrackId (the job does that)
        var job = new AnalysisJob(
            videoPath,
            _decoderFactory,
            detector,
            _repository,
            sampling,
            _trackerFactory?.Invoke(),
            _options,
            _timeProvider,
            _loggerFactory.CreateLogger<AnalysisJob>());
        _current = job;
        _jobs.Start(job);
        return job;
    }
}
