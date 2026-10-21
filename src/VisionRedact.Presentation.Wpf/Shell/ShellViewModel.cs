using System.IO;
using Microsoft.Extensions.Logging;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Regions;
using Prism.Services.Dialogs;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Export;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.Playback;
using VisionRedact.Application.Projects;
using VisionRedact.Application.Redaction;
using VisionRedact.Application.Workspace;
using VisionRedact.Domain.Jobs;
using VisionRedact.Presentation.Wpf.Events;
using VisionRedact.Presentation.Wpf.Features.Detections;
using VisionRedact.Presentation.Wpf.Features.Dialogs;
using VisionRedact.Presentation.Wpf.Infrastructure;

namespace VisionRedact.Presentation.Wpf.Shell;

// an Inspector tab: the Prism view to show, and its label
public sealed record InspectorTab(string ViewName, string Title)
{
    // = the tab item's UIA name (screen readers, UI tests)
    public override string ToString() => Title;
}

// the main window's view model: Open / Save / Export / Cancel, title, status bar, export progress.
// talks to Application services only (no Emgu, ONNX or JSON) and turns their exceptions into dialogs.
// UI thread. export news comes from the job's thread and is posted back before any property changes
public sealed class ShellViewModel : BindableBase
{
    private const string AppName = "VisionRedact Studio";

    private readonly VideoWorkspace _workspace;
    private readonly IMediaRuntimeProbe _runtimeProbe;
    private readonly IFileDialogService _fileDialog;
    private readonly IDialogService _dialogs;
    private readonly IEventAggregator _events;
    private readonly IRegionManager _regionManager;
    private readonly IProcessingJobManager _jobs;
    private readonly DetectorHost _detectorHost;
    private readonly DetectionPreferences _detectionPreferences;
    private readonly ProjectService _projects;
    private readonly IVideoExportService _export;
    private readonly IDetectionRepository _detections;
    private readonly IRedactionRegionStore _regions;
    private readonly IUserPrompt _prompt;
    private readonly IUiThread _ui;
    private readonly ILogger<ShellViewModel> _logger;

    private string _title = AppName;
    private string _statusText = "Starting…";
    private string _runtimeStatusText = "Loading OpenCV runtime…";
    private string _runtimeStatusDetail = string.Empty;
    private bool _isRuntimeAvailable;
    private bool _isRuntimeChecked;
    private InspectorTab? _selectedInspectorTab;
    // the latest export (running or done): for its state and for Cancel
    private ExportJob? _exportJob;
    private string _jobText = string.Empty;
    private double _jobProgress;

    public ShellViewModel(
        VideoWorkspace workspace,
        IMediaRuntimeProbe runtimeProbe,
        IFileDialogService fileDialog,
        IDialogService dialogs,
        IEventAggregator events,
        IRegionManager regionManager,
        IProcessingJobManager jobs,
        DetectorHost detectorHost,
        DetectionPreferences detectionPreferences,
        ProjectService projects,
        IVideoExportService export,
        IDetectionRepository detections,
        IRedactionRegionStore regions,
        IUserPrompt prompt,
        IUiThread ui,
        ILogger<ShellViewModel> logger)
    {
        _workspace = workspace;
        _runtimeProbe = runtimeProbe;
        _fileDialog = fileDialog;
        _dialogs = dialogs;
        _events = events;
        _regionManager = regionManager;
        _jobs = jobs;
        _detectorHost = detectorHost;
        _detectionPreferences = detectionPreferences;
        _projects = projects;
        _export = export;
        _detections = detections;
        _regions = regions;
        _prompt = prompt;
        _ui = ui;
        _logger = logger;

        // Open needs the OpenCV runtime, Save / Export need a video, one export at a time
        OpenVideoCommand = new AsyncCommand(OpenVideoAsync, OnUnexpectedError, () => IsRuntimeAvailable);
        OpenProjectCommand = new AsyncCommand(OpenProjectAsync, OnUnexpectedError, () => IsRuntimeAvailable);
        SaveProjectCommand = new AsyncCommand(SaveProjectAsync, OnUnexpectedError, () => HasVideo);
        ExportCommand = new AsyncCommand(ExportAsync, OnUnexpectedError, () => HasVideo && !IsExporting);
        CancelExportCommand = new DelegateCommand(() => _exportJob?.Cancel(), () => IsExporting);
        InspectorTabs =
        [
            new(ViewNames.Metadata, "Video"),
            new(ViewNames.Detections, "Detections"),
            new(ViewNames.Redaction, "Redaction"),
            new(ViewNames.People, "People"),
            new(ViewNames.Diagnostics, "Diagnostics"),
        ];
    }

    public AsyncCommand OpenVideoCommand { get; }

    public AsyncCommand OpenProjectCommand { get; }

    public AsyncCommand SaveProjectCommand { get; }

    public AsyncCommand ExportCommand { get; }

    public DelegateCommand CancelExportCommand { get; }

    public IReadOnlyList<InspectorTab> InspectorTabs { get; }

    /// <summary>Setting it navigates the Inspector region (two-way bound to the tab strip).</summary>
    public InspectorTab? SelectedInspectorTab
    {
        get => _selectedInspectorTab;
        set
        {
            if (value is not null && SetProperty(ref _selectedInspectorTab, value))
            {
                NavigateInspector(value.ViewName);
            }
        }
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string RuntimeStatusText
    {
        get => _runtimeStatusText;
        private set => SetProperty(ref _runtimeStatusText, value);
    }

    // the full runtime report (versions, video backends, or the error), as the status bar's tooltip
    public string RuntimeStatusDetail
    {
        get => _runtimeStatusDetail;
        private set => SetProperty(ref _runtimeStatusDetail, value);
    }

    public bool IsRuntimeAvailable
    {
        get => _isRuntimeAvailable;
        private set
        {
            if (SetProperty(ref _isRuntimeAvailable, value))
            {
                // ShowRuntimeError and both Open commands depend on it
                RaisePropertyChanged(nameof(ShowRuntimeError));
                OpenVideoCommand.RaiseCanExecuteChanged();
                OpenProjectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // the banner only after the check ran, not during start-up
    public bool ShowRuntimeError => _isRuntimeChecked && !_isRuntimeAvailable;

    public bool HasVideo => _workspace.CurrentSession is not null;

    // a job exists and isn't Completed / Cancelled / Failed yet
    public bool IsExporting => _exportJob is { } job && !JobStateMachine.IsTerminal(job.State);

    public string JobText
    {
        get => _jobText;
        private set => SetProperty(ref _jobText, value);
    }

    public double JobProgress
    {
        get => _jobProgress;
        private set => SetProperty(ref _jobProgress, value);
    }

    /// <summary>Called once from the shell's Loaded handler.</summary>
    public async Task InitializeAsync()
    {
        // first tab = Video (the metadata view)
        SelectedInspectorTab = InspectorTabs[0];

        // loading OpenCV's native dll is slow, blocking work: on the pool, so the window paints at once.
        // the await comes back to the UI thread
        var status = await Task.Run(_runtimeProbe.Probe);
        _isRuntimeChecked = true;
        // short in the status bar so it's never cut off, the full report is the tooltip
        RuntimeStatusDetail = status.IsAvailable ? status.Summary : $"{status.Summary}: {status.ErrorDetail}";
        RuntimeStatusText = !status.IsAvailable
            ? status.Summary
            : status.VideoBackends.Contains("FFMPEG", StringComparer.OrdinalIgnoreCase) ? "OpenCV ready · FFmpeg backend" : "OpenCV ready";
        IsRuntimeAvailable = status.IsAvailable;
        StatusText = status.IsAvailable ? "Ready — open a video (Ctrl+O) or a project (Ctrl+Shift+O)." : "Video features are disabled: the native OpenCV runtime is unavailable.";
        // the models folder and the GPUs, for the Detections tab
        await _detectionPreferences.RefreshAsync();

        // VisionRedactStudio.exe <video|project> opens it directly (demo scripts, "Open with"). arg 0 is the exe
        var startupPath = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
        if (status.IsAvailable && startupPath is not null)
        {
            if (startupPath.EndsWith(ProjectService.Extension, StringComparison.OrdinalIgnoreCase))
            {
                await OpenProjectPathAsync(startupPath);
            }
            else
            {
                await OpenPathAsync(startupPath);
            }
        }
    }

    /// <summary>Called from the shell's Closing handler; awaited so native resources are released before exit.</summary>
    public async Task ShutdownAsync()
    {
        // order matters: jobs use decoders and the loaded models, so stop them before freeing what they use
        try
        {
            await _jobs.CancelAllAsync();
            await _workspace.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while shutting down the workspace.");
        }
        finally
        {
            _detectorHost.Dispose(); // unloads the ONNX sessions (native memory)
        }
    }

    // null = the user canceled
    private async Task OpenVideoAsync()
    {
        var path = _fileDialog.PickVideoFile();
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    private async Task OpenPathAsync(string path)
    {
        var fileName = Path.GetFileName(path);
        StatusText = $"Opening {fileName}…";
        try
        {
            // the workspace opens the decoder off the UI thread and returns the new session
            OnVideoOpened(await _workspace.OpenAsync(path));
        }
        catch (MediaException ex)
        {
            // expected (bad file, missing codec, missing native runtime): log it, show a friendly dialog.
            // anything else is unexpected and goes up (from the Open button: AsyncCommand -> OnUnexpectedError)
            _logger.LogWarning(ex, "Could not open {Path} ({Kind}).", path, ex.Kind);
            StatusText = $"Could not open {fileName}.";
            ShowError("Could not open video", ex.Message, ex.Kind == MediaErrorKind.NativeRuntimeUnavailable ? ex.ToString() : ex.InnerException?.Message);
        }
    }

    private async Task OpenProjectAsync()
    {
        var path = _fileDialog.PickProjectToOpen();
        if (path is not null)
        {
            await OpenProjectPathAsync(path);
        }
    }

    // a saved .vrproject: detections and review state, regions, settings, people, and the path of its video
    private async Task OpenProjectPathAsync(string path)
    {
        try
        {
            StatusText = $"Opening project {Path.GetFileName(path)}…";
            var result = await _projects.OpenAsync(path);
            // video moved or renamed since saving: let the user find it, then relink
            if (result.SourceMissing)
            {
                ShowInfo("Video not found", $"The project's video was not found at:\n{result.Document.SourcePath}\n\nPlease locate it (it may have been moved or renamed).");
                var relocated = _fileDialog.PickVideoFile("Locate the project's video");
                if (relocated is null)
                {
                    StatusText = "Project not opened: video not located.";
                    return;
                }

                result = await _projects.RelinkAsync(result.Document, relocated);
            }

            OnVideoOpened(result.Session!);
            StatusText = $"Project opened: {result.Document.Detections.Count:N0} detections, {result.Document.Regions.Count} regions.";
            // the video is there but isn't the same file (fingerprint): warn, frame numbers may not match anymore
            if (result.SourceChanged)
            {
                ShowInfo("Video differs from the project", "The video's size, resolution or frame count differ from when the project was saved. Review detections before exporting.");
            }
        }
        catch (ProjectException ex)
        {
            _logger.LogWarning(ex, "Could not open project {Path}.", path);
            ShowError("Could not open project", ex.Message, ex.InnerException?.Message);
        }
        catch (MediaException ex)
        {
            ShowError("Could not open the project's video", ex.Message, ex.InnerException?.Message);
        }
    }

    // the review state, plus the selected model names for the record
    private async Task SaveProjectAsync()
    {
        if (_workspace.CurrentSession is not { } session)
        {
            return;
        }

        var path = _fileDialog.PickProjectSavePath(Path.GetFileNameWithoutExtension(session.Metadata.SourcePath) + ProjectService.Extension);
        if (path is null)
        {
            return;
        }

        try
        {
            await _projects.SaveAsync(path, _detectionPreferences.SelectedModels.Select(m => m.DisplayName).ToArray());
            StatusText = $"Project saved to {path}.";
        }
        catch (ProjectException ex)
        {
            ShowError("Could not save project", ex.Message, ex.InnerException?.Message);
        }
    }

    // snapshot -> coverage check -> pick a file -> start the export job. returns at once, progress and the
    // result come later as job events
    private Task ExportAsync()
    {
        if (_workspace.CurrentSession is not { } session)
        {
            return Task.CompletedTask;
        }

        // snapshot now: the file matches the review at the moment Export was pressed, whatever comes next.
        // an immutable copy, so the export thread reads it w/o locks while the UI keeps editing
        var m = session.Metadata;
        var snapshot = RedactionSnapshot.Capture(m.FrameCount, _detections, _regions);

        // careful: the fail-safe rule (Pending is blurred) only covers faces the AI found. frames the scan never
        // reached have no boxes, so faces there would go out unblurred. ask first, No is the default
        if (!snapshot.Coverage.IsComplete
            && !_prompt.Confirm(ExportMessages.CoverageWarningTitle, ExportMessages.CoverageWarning(snapshot.Coverage, m.FramesPerSecond)))
        {
            StatusText = "Export not started: scan the video for faces first (Analyze video).";
            return Task.CompletedTask;
        }

        var target = _fileDialog.PickExportPath(Path.GetFileNameWithoutExtension(m.SourcePath) + "-redacted");
        if (target is not { } choice)
        {
            return Task.CompletedTask;
        }

        var job = new ExportJob(_export, new ExportRequest(m.SourcePath, choice.Path, choice.Format, snapshot));
        _exportJob = job;
        // raised on the export's thread: Post before touching bound properties
        job.ProgressChanged += (_, p) => _ui.Post(() =>
        {
            JobProgress = p.Fraction;
            JobText = $"Exporting: {p.Message}";
        });
        // same for the state changes
        job.StateChanged += (_, e) => _ui.Post(() => OnExportState(job, e));
        // the job manager runs it and tracks it, so shutdown can cancel it
        _jobs.Start(job);
        RaiseExportState();
        JobText = "Exporting…";
        return Task.CompletedTask;
    }

    // UI thread (posted). buttons, texts, and the final result for the user
    private void OnExportState(ExportJob job, JobStateChangedEventArgs e)
    {
        RaiseExportState();
        // still running or canceling: text only, no dialog yet
        if (!JobStateMachine.IsTerminal(e.Current))
        {
            JobText = e.Current == ProcessingJobState.Cancelling ? "Cancelling export…" : JobText;
            return;
        }

        // done: clear the bar, publish JobCompletedEvent.
        // TODO: nothing subscribes to JobCompletedEvent yet. ok for now
        JobText = string.Empty;
        JobProgress = 0;
        _events.GetEvent<JobCompletedEvent>().Publish(job);
        switch (e.Current)
        {
            case ProcessingJobState.Completed when job.Result is { } r:
                var coverage = job.Request.Redactions.Coverage;
                var warnings = ExportMessages.HasWarnings(r, coverage);
                StatusText = $"Exported {r.FramesWritten:N0} frames to {r.OutputPath}{(warnings ? " (with warnings)" : string.Empty)}.";
                if (r.SourceEndedEarly)
                {
                    ShowError("Export complete, but the source ended early", ExportMessages.Completed(r, coverage), null);
                }
                else
                {
                    ShowInfo(warnings ? "Export complete, with a warning" : "Export complete", ExportMessages.Completed(r, coverage));
                }

                break;
            case ProcessingJobState.Cancelled:
                StatusText = "Export cancelled. No partial file was left behind.";
                break;
            case ProcessingJobState.Failed:
                ShowError("Export failed", e.Error?.Message ?? "Unknown error", e.Error?.ToString());
                break;
        }
    }

    // title, status, then VideoOpenedEvent so the player and the tabs attach to the new session, then the commands
    // that need a video
    private void OnVideoOpened(PlaybackSession session)
    {
        var fileName = Path.GetFileName(session.Metadata.SourcePath);
        Title = $"{fileName} — {AppName}";
        StatusText = $"Loaded {fileName} ({session.Metadata.Width}×{session.Metadata.Height}, {session.Metadata.FramesPerSecond:0.###} fps, decoder: {session.DecoderBackend}).";
        _events.GetEvent<VideoOpenedEvent>().Publish(session);
        RaisePropertyChanged(nameof(HasVideo));
        SaveProjectCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private void RaiseExportState()
    {
        RaisePropertyChanged(nameof(IsExporting));
        ExportCommand.RaiseCanExecuteChanged();
        CancelExportCommand.RaiseCanExecuteChanged();
    }

    // shows the named view in the Inspector region, logs a failed navigation
    private void NavigateInspector(string viewName)
    {
        _regionManager.RequestNavigate(RegionNames.Inspector, viewName, result =>
        {
            if (result.Result != true)
            {
                _logger.LogWarning(result.Error, "Navigation to {View} failed.", viewName);
            }
        });
    }

    private void OnUnexpectedError(Exception ex)
    {
        _logger.LogError(ex, "Unexpected error in a shell command.");
        StatusText = "An unexpected error occurred.";
        ShowError("Unexpected error", ex.Message, ex.ToString());
    }

    // one dialog view for both, isError picks the icon
    private void ShowError(string title, string message, string? details) =>
        _dialogs.ShowDialog(DialogNames.Error, ErrorDialogViewModel.CreateParameters(title, message, details), _ => { });

    private void ShowInfo(string title, string message) =>
        _dialogs.ShowDialog(DialogNames.Error, ErrorDialogViewModel.CreateParameters(title, message, null, isError: false), _ => { });
}
