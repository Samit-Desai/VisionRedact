using Microsoft.Extensions.Logging;
using VisionRedact.Application.Jobs;

namespace VisionRedact.Application.Export;

// Export button -> ShellViewModel -> ProcessingJobManager.Start(this) -> IVideoExportService does the work.
// this only adds Cancel, the states, and the progress text for the status bar
public sealed class ExportJob : ProcessingJob
{
    private readonly IVideoExportService _export;
    private readonly ExportRequest _request;

    public ExportJob(IVideoExportService export, ExportRequest request, ILogger<ExportJob>? logger = null)
        : base($"Export to {Path.GetFileName(request.OutputPath)}", logger: logger)
    {
        _export = export;
        _request = request;
    }

    public ExportRequest Request => _request;

    public ExportResult? Result { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // reports arrive on the export's own threads and go straight to ReportProgress, which throttles them
        var progress = new SynchronousProgress(p =>
            ReportProgress(p.FramesWritten, p.TotalFrames, $"{p.FramesWritten:N0} frames written, {p.FramesRedacted:N0} redacted"));
        Result = await _export.ExportAsync(_request, progress, cancellationToken).ConfigureAwait(false);

        // force, so the throttle can't eat the final "Done"
        ReportProgress(Result.FramesWritten, Result.FramesWritten, $"Done: {Result.FramesWritten:N0} frames ({Result.Codec})", force: true);
    }

    // Progress<T> would post every report to the pool, this calls inline
    private sealed class SynchronousProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
