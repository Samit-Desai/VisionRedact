using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Regions;
using VisionRedact.Application.Analysis;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Workspace;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Jobs;
using VisionRedact.Presentation.Wpf.Events;
using VisionRedact.Presentation.Wpf.Infrastructure;

namespace VisionRedact.Presentation.Wpf.Features.Detections;

public enum DetectionFilter
{
    All,
    Pending,
    Approved,
    Rejected,
}

// the Detections tab: models, the scan, and the review list.
// repository.Changed (scan thread, a batch per scanned frame) -> _pending -> Flush on a UI timer -> Items.
// the handler only enqueues and the timer drains 4x a second, so the Dispatcher queue can't grow w/o limit
public sealed class DetectionsViewModel : BindableBase, INavigationAware
{
    private readonly DetectionPreferences _preferences;
    private readonly AnalysisService _analysis;
    private readonly FrameDetectionService _frameDetection;
    private readonly IDetectionRepository _repository;
    private readonly VideoWorkspace _workspace;
    private readonly IEventAggregator _events;
    private readonly IUiThread _ui;
    private readonly ILogger<DetectionsViewModel> _logger;
    private readonly ConcurrentQueue<DetectionsChangedEventArgs> _pending = new();
    private readonly Dictionary<Guid, DetectionItemViewModel> _byId = [];
    private readonly Dictionary<ObjectClass, int> _numbers = [];
    private readonly DispatcherTimer _flushTimer;
    private DetectionItemViewModel? _selected;
    private DetectionFilter _filter = DetectionFilter.All;
    private string _status = "Open a video, choose a model and analyze.";
    private string _statistics = string.Empty;
    private double _progress;
    private bool _isAnalyzing;
    private AnalysisJob? _job;

    public DetectionsViewModel(
        DetectionPreferences preferences,
        AnalysisService analysis,
        FrameDetectionService frameDetection,
        IDetectionRepository repository,
        VideoWorkspace workspace,
        IEventAggregator events,
        IUiThread ui,
        ILogger<DetectionsViewModel> logger)
    {
        _preferences = preferences;
        _analysis = analysis;
        _frameDetection = frameDetection;
        _repository = repository;
        _workspace = workspace;
        _events = events;
        _ui = ui;
        _logger = logger;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is DetectionItemViewModel d && Matches(d);

        AnalyzeCommand = new AsyncCommand(AnalyzeAsync, OnError, () => HasVideo && !_isAnalyzing);
        DetectCurrentFrameCommand = new AsyncCommand(DetectCurrentFrameAsync, OnError, () => HasVideo);
        CancelCommand = new DelegateCommand(() => _job?.Cancel(), () => _isAnalyzing);
        ApproveAllPendingCommand = new DelegateCommand(() => Set("Approved", _repository.UpdateStatusWhere(d => d.Status == ReviewStatus.Pending, ReviewStatus.Approved)));
        GoToCommand = new DelegateCommand<DetectionItemViewModel>(d => _events.GetEvent<SeekRequestedEvent>().Publish(d.FrameIndex));
        DeleteCommand = new DelegateCommand<DetectionItemViewModel>(d => _repository.Remove(d.Id));
        RefreshModelsCommand = new AsyncCommand(_preferences.RefreshAsync, OnError);
        ApproveTrackCommand = new DelegateCommand(() => SetTrack(ReviewStatus.Approved), () => _selected?.Detection.TrackId is not null);
        RejectTrackCommand = new DelegateCommand(() => SetTrack(ReviewStatus.Rejected), () => _selected?.Detection.TrackId is not null);

        _repository.Changed += (_, e) => _pending.Enqueue(e); // any thread: enqueue only
        _events.GetEvent<VideoOpenedEvent>().Subscribe(_ => RaiseCommandStates(), ThreadOption.UIThread);

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Flush(), System.Windows.Application.Current.Dispatcher);
        _flushTimer.Start();
        Rebuild();
    }

    public DetectionPreferences Preferences => _preferences;

    public ObservableCollection<DetectionItemViewModel> Items { get; } = [];

    public ICollectionView ItemsView { get; }

    public IReadOnlyList<DetectionFilter> Filters { get; } = Enum.GetValues<DetectionFilter>();

    public AsyncCommand AnalyzeCommand { get; }

    public AsyncCommand DetectCurrentFrameCommand { get; }

    public DelegateCommand CancelCommand { get; }

    public DelegateCommand ApproveAllPendingCommand { get; }

    public DelegateCommand<DetectionItemViewModel> GoToCommand { get; }

    public DelegateCommand<DetectionItemViewModel> DeleteCommand { get; }

    public AsyncCommand RefreshModelsCommand { get; }

    // a whole track in one click, not a box per frame
    public DelegateCommand ApproveTrackCommand { get; }

    public DelegateCommand RejectTrackCommand { get; }

    public bool HasVideo => _workspace.CurrentSession is not null;

    public DetectionFilter Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                ItemsView.Refresh();
            }
        }
    }

    public DetectionItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                _events.GetEvent<SelectedDetectionChangedEvent>().Publish(value?.Detection);
                ApproveTrackCommand.RaiseCanExecuteChanged();
                RejectTrackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Statistics
    {
        get => _statistics;
        private set => SetProperty(ref _statistics, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string Summary => string.Create(CultureInfo.CurrentCulture,
        $"{Items.Count:N0} detections · {Items.Select(i => i.Detection.TrackId).Where(t => t is not null).Distinct().Count():N0} tracks · {Items.Count(i => i.Status == ReviewStatus.Pending):N0} pending · {Items.Count(i => i.Status == ReviewStatus.Rejected):N0} rejected");

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;

    public void OnNavigatedTo(NavigationContext navigationContext)
    {
        RaiseCommandStates();
        if (_preferences.Models.Count == 0)
        {
            RefreshModelsCommand.Execute(null);
        }
    }

    public void OnNavigatedFrom(NavigationContext navigationContext)
    {
    }

    private async Task AnalyzeAsync()
    {
        if (_workspace.CurrentSession is not { } session)
        {
            return;
        }

        var models = _preferences.SelectedModels;
        if (models.Count == 0)
        {
            Status = "Select at least one installed model.";
            return;
        }

        Status = "Loading model…";
        var job = await _analysis.StartAsync(session.Metadata.SourcePath, models, _preferences.Sampling.Create(session.Metadata.FramesPerSecond));
        _job = job;
        IsAnalyzing = true;
        Status = job.Name;
        job.ProgressChanged += (_, p) => _ui.Post(() => OnProgress(job, p));
        job.StateChanged += (_, e) => _ui.Post(() => OnJobState(job, e));
    }

    private async Task DetectCurrentFrameAsync()
    {
        if (_workspace.CurrentSession is not { } session)
        {
            return;
        }

        // the frame the timeline shows, even if a slow seek hasn't drawn it yet
        var frame = Math.Max(0, session.RequestedFrameIndex);
        Status = $"Detecting on frame {frame + 1:N0}…";
        var found = await _frameDetection.DetectFrameAsync(session.Metadata.SourcePath, frame, _preferences.SelectedModels);
        Status = $"Frame {frame + 1:N0}: {found.Count} detection(s).";
    }

    private void OnProgress(AnalysisJob job, JobProgress progress)
    {
        if (!ReferenceEquals(job, _job))
        {
            return;
        }

        Progress = progress.Fraction;
        Status = progress.Message;
        var stats = job.GetStatistics();
        Statistics = string.Create(CultureInfo.CurrentCulture,
            $"{progress.Fraction:P1} scanned · inference {stats.AverageInferenceTime.TotalMilliseconds:0} ms/frame · queue {stats.ChannelCount}/{stats.ChannelCapacity}");
    }

    private void OnJobState(AnalysisJob job, JobStateChangedEventArgs e)
    {
        if (!ReferenceEquals(job, _job))
        {
            return;
        }

        IsAnalyzing = !JobStateMachine.IsTerminal(e.Current);
        Status = e.Current switch
        {
            ProcessingJobState.Cancelling => "Cancelling… (finishing the current inference)",
            ProcessingJobState.Cancelled => $"Cancelled. {job.Progress.Message}. Results so far are kept.",
            ProcessingJobState.Completed => $"Completed. {job.Progress.Message}.",
            ProcessingJobState.Failed => $"Failed: {e.Error?.Message}",
            _ => Status,
        };
        if (JobStateMachine.IsTerminal(e.Current))
        {
            _events.GetEvent<JobCompletedEvent>().Publish(job);
        }
    }

    // UI thread, every 250 ms
    private void Flush()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var reset = false;
        var batches = new List<DetectionsChangedEventArgs>();
        while (_pending.TryDequeue(out var change))
        {
            reset |= change.IsReset;
            batches.Add(change);
        }

        if (reset)
        {
            Rebuild();
            return;
        }

        foreach (var change in batches)
        {
            foreach (var id in change.Removed)
            {
                if (_byId.Remove(id, out var item))
                {
                    Items.Remove(item);
                }
            }

            foreach (var detection in change.Added)
            {
                // a change raised between Rebuild's queue clear and its snapshot comes twice, so dedupe by id
                if (_byId.TryGetValue(detection.Id, out var existing))
                {
                    existing.Update(detection);
                }
                else
                {
                    Insert(Create(detection));
                }
            }

            foreach (var detection in change.Updated)
            {
                if (_byId.TryGetValue(detection.Id, out var item))
                {
                    item.Update(detection);
                }
            }
        }

        if (_filter != DetectionFilter.All && batches.Any(b => b.Updated.Count > 0))
        {
            ItemsView.Refresh();
        }

        RaisePropertyChanged(nameof(Summary));
    }

    private void Rebuild()
    {
        _pending.Clear();
        _byId.Clear();
        _numbers.Clear();
        Items.Clear();
        foreach (var detection in _repository.Snapshot())
        {
            var item = Create(detection);
            Items.Add(item); // snapshot is already frame-ordered
        }

        RaisePropertyChanged(nameof(Summary));
    }

    private DetectionItemViewModel Create(Detection detection)
    {
        var number = _numbers[detection.Class] = _numbers.GetValueOrDefault(detection.Class) + 1;
        var time = _workspace.CurrentSession?.Metadata.TimeOfFrame(detection.FrameIndex) ?? TimeSpan.Zero;
        var item = new DetectionItemViewModel(detection, number, time, (d, status) => _repository.UpdateStatus(d.Id, status));
        _byId[detection.Id] = item;
        return item;
    }

    // keeps frame order. the scan comes in order (append), "detect this frame" needs the binary insert
    private void Insert(DetectionItemViewModel item)
    {
        if (Items.Count == 0 || Items[^1].FrameIndex <= item.FrameIndex)
        {
            Items.Add(item);
            return;
        }

        int lo = 0, hi = Items.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Items[mid].FrameIndex <= item.FrameIndex)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        Items.Insert(lo, item);
    }

    private bool Matches(DetectionItemViewModel item) => _filter switch
    {
        DetectionFilter.Pending => item.Status == ReviewStatus.Pending,
        DetectionFilter.Approved => item.Status == ReviewStatus.Approved,
        DetectionFilter.Rejected => item.Status == ReviewStatus.Rejected,
        _ => true,
    };

    private void Set(string what, int count) => Status = $"{what} {count:N0} detection(s).";

    private void SetTrack(ReviewStatus status)
    {
        if (_selected?.Detection.TrackId is { } track)
        {
            Set(status == ReviewStatus.Approved ? $"Track {track}: approved" : $"Track {track}: rejected", _repository.UpdateStatusWhere(d => d.TrackId == track, status));
        }
    }

    private void RaiseCommandStates()
    {
        RaisePropertyChanged(nameof(HasVideo));
        AnalyzeCommand.RaiseCanExecuteChanged();
        DetectCurrentFrameCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private void OnError(Exception ex)
    {
        _logger.LogError(ex, "Detection command failed.");
        IsAnalyzing = _analysis.IsRunning;
        Status = ex is DetectionException detection ? detection.Message : "Detection failed: " + ex.Message;
    }
}
