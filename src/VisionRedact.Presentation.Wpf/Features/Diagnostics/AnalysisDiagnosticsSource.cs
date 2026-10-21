using System.Globalization;
using VisionRedact.Application.Analysis;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Diagnostics;

namespace VisionRedact.Presentation.Wpf.Features.Diagnostics;

// the "AI analysis" rows. Sample runs on the UI thread twice a second and only reads the current scan's counters
public sealed class AnalysisDiagnosticsSource(AnalysisService analysis, IDetectionRepository repository) : IDiagnosticsSource
{
    private readonly RateMeter _analyzedRate = new(TimeSpan.FromSeconds(2));
    private readonly RateMeter _scannedRate = new(TimeSpan.FromSeconds(2));
    // a new job counts from 0 again
    private AnalysisJob? _lastJob;

    public string Group => "AI analysis";

    public IReadOnlyList<DiagnosticsMetric> Metrics { get; } =
    [
        new("aiState", "Job state", "Created → Running → Completed/Failed, or Cancelling → Cancelled"),
        new("aiDevice", "Execution provider", "Where inference runs: CPU or DirectML on a specific GPU"),
        new("aiFps", "AI FPS", "Frames through inference per second (measured)", StartsSection: true),
        new("aiDecodeFps", "Analysis decode FPS", "Frames scanned by the analysis decoder per second (sampled + skipped)"),
        new("aiLatency", "Inference latency", "Mean time of one Detect() call (preprocess + ONNX Run + decode + NMS)", StartsSection: true),
        new("aiQueue", "Channel occupancy", "Bounded channel count / capacity. Full = decoder waiting on inference (backpressure)"),
        new("aiFrames", "Frames analyzed", "Frames that went through a model", StartsSection: true),
        new("aiDetections", "Detections stored", "Total detections in the repository"),
    ];

    public IEnumerable<(string Key, string Value)> Sample(TimeSpan timestamp)
    {
        var c = CultureInfo.CurrentCulture;
        var job = analysis.Current;
        // another job: new rate windows
        if (!ReferenceEquals(job, _lastJob))
        {
            _lastJob = job;
            _analyzedRate.Reset();
            _scannedRate.Reset();
        }

        yield return ("aiDetections", repository.Count.ToString("N0", c));
        if (job is null)
        {
            yield return ("aiState", "Not started");
            yield break;
        }

        // one snapshot, the job's workers update these on their own threads
        var stats = job.GetStatistics();
        yield return ("aiState", job.State.ToString());
        yield return ("aiDevice", job.Detector.ExecutionProvider);
        yield return ("aiFps", DiagnosticsViewModel.Rate(_analyzedRate.Add(stats.FramesAnalyzed, timestamp), c));
        yield return ("aiDecodeFps", DiagnosticsViewModel.Rate(_scannedRate.Add(stats.FramesScanned, timestamp), c));
        yield return ("aiLatency", stats.AverageInferenceTime.TotalMilliseconds.ToString("0.0 ms", c));
        yield return ("aiQueue", $"{stats.ChannelCount} / {stats.ChannelCapacity}");
        yield return ("aiFrames", stats.FramesAnalyzed.ToString("N0", c));
    }
}
