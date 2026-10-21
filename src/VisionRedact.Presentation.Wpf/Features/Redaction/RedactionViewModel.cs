using System.Collections.ObjectModel;
using System.ComponentModel;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Regions;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.Redaction;
using VisionRedact.Application.Tracking;
using VisionRedact.Application.Workspace;
using VisionRedact.Domain.Jobs;
using VisionRedact.Domain.Redaction;
using VisionRedact.Presentation.Wpf.Events;
using VisionRedact.Presentation.Wpf.Infrastructure;

namespace VisionRedact.Presentation.Wpf.Features.Redaction;

public sealed record RegionItem(RedactionRegion Region, string Title, string Range);

// the Redaction tab: blur settings + hand-drawn regions, both kept in IRedactionRegionStore.
// draws nothing itself, the player and the export read the same store.
// edit -> store.Update -> Changed -> Post(Rebuild) -> list. regions are few, so the list is just rebuilt
public sealed class RedactionViewModel : BindableBase, INavigationAware
{
    private readonly IRedactionRegionStore _store;
    private readonly VideoWorkspace _workspace;
    private readonly IEventAggregator _events;
    private readonly IUiThread _ui;
    private readonly IVideoDecoderFactory _decoders;
    private readonly IVisualTrackerFactory _trackers;
    private readonly IProcessingJobManager _jobs;
    private RegionItem? _selected;
    private RegionTrackingJob? _trackingJob;
    private string _trackingStatus = string.Empty;
    // true while code sets the selection (rebuild / editor sync), so it isn't echoed back to Editor
    private bool _syncing;

    public RedactionViewModel(
        IRedactionRegionStore store,
        RedactionEditorState editor,
        VideoWorkspace workspace,
        IEventAggregator events,
        IUiThread ui,
        IVideoDecoderFactory decoders,
        IVisualTrackerFactory trackers,
        IProcessingJobManager jobs)
    {
        _decoders = decoders;
        _trackers = trackers;
        _jobs = jobs;
        _store = store;
        Editor = editor;
        _workspace = workspace;
        _events = events;
        _ui = ui;

        // all need a selected region. start never passes end
        SetStartCommand = new DelegateCommand(() => UpdateSelected(r => r with { StartFrame = Math.Min(CurrentFrame, r.EndFrame) }), () => _selected is not null);
        SetEndCommand = new DelegateCommand(() => UpdateSelected(r => r with { EndFrame = Math.Max(CurrentFrame, r.StartFrame) }), () => _selected is not null);
        WholeVideoCommand = new DelegateCommand(() => UpdateSelected(r => r with { StartFrame = 0, EndFrame = Math.Max(0, _workspace.CurrentSession?.Metadata.LastFrameIndex ?? r.EndFrame) }), () => _selected is not null);
        DeleteCommand = new DelegateCommand(() => { if (_selected is not null) { _store.Remove(_selected.Region.Id); } }, () => _selected is not null);
        // seeking is the player's job, so just publish
        GoToStartCommand = new DelegateCommand(() => { if (_selected is not null) { _events.GetEvent<SeekRequestedEvent>().Publish(_selected.Region.StartFrame); } }, () => _selected is not null);

        TrackCommand = new DelegateCommand(StartTracking, () => _selected is not null && !IsTracking);
        CancelTrackingCommand = new DelegateCommand(() => _trackingJob?.Cancel(), () => IsTracking);
        // remember: the tracking job writes the store from its own thread, and Regions is bound (UI thread only)
        _store.Changed += (_, _) => _ui.Post(Rebuild);
        // e.g. a region clicked on the video
        Editor.PropertyChanged += OnEditorChanged;
        Rebuild();
    }

    // shared w/ the player: selection, draw mode, preview on/off
    public RedactionEditorState Editor { get; }

    public ObservableCollection<RegionItem> Regions { get; } = [];

    public IReadOnlyList<RedactionEffectKind> Effects { get; } = Enum.GetValues<RedactionEffectKind>();

    public DelegateCommand SetStartCommand { get; }

    public DelegateCommand SetEndCommand { get; }

    public DelegateCommand WholeVideoCommand { get; }

    public DelegateCommand DeleteCommand { get; }

    public DelegateCommand GoToStartCommand { get; }

    /// <summary>Make the selected region follow the object through its range (OpenCV KCF).</summary>
    public DelegateCommand TrackCommand { get; }

    public DelegateCommand CancelTrackingCommand { get; }

    public bool IsTracking => _trackingJob is { } job && !JobStateMachine.IsTerminal(job.State);

    public string TrackingStatus
    {
        get => _trackingStatus;
        private set => SetProperty(ref _trackingStatus, value);
    }

    public RegionItem? SelectedRegion
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                // a pick in the list goes to Editor, so the player highlights it too.
                // not while syncing: then it came from Editor
                if (!_syncing)
                {
                    Editor.SelectedRegionId = value?.Region.Id;
                }

                RaisePropertyChanged(nameof(SelectedEffect));
                RaisePropertyChanged(nameof(HasSelection));
                SetStartCommand.RaiseCanExecuteChanged();
                SetEndCommand.RaiseCanExecuteChanged();
                WholeVideoCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
                GoToStartCommand.RaiseCanExecuteChanged();
                TrackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    // writes the store, then Changed rebuilds the list
    public RedactionEffectKind SelectedEffect
    {
        get => _selected?.Region.Effect ?? RedactionEffectKind.GaussianBlur;
        set => UpdateSelected(r => r with { Effect = value });
    }

    // settings: % in the UI, 0..1 fractions in the store
    public RedactionEffectKind DetectionEffect
    {
        get => _store.Settings.DetectionEffect;
        set => _store.Settings = _store.Settings with { DetectionEffect = value };
    }

    // AI boxes only, grown on each side
    public double PaddingPercent
    {
        get => _store.Settings.Padding * 100;
        set => _store.Settings = _store.Settings with { Padding = Math.Clamp(value / 100, 0, 0.5) };
    }

    // one strength for AI boxes and regions alike
    public double StrengthPercent
    {
        get => _store.Settings.Strength * 100;
        set => _store.Settings = _store.Settings with { Strength = Math.Clamp(value / 100, 0, 1) };
    }

    // the frame the user asked for, even if a slow seek hasn't drawn it yet. 0 w/o a video.
    // was the frame on screen once: during a slow 4K seek "Start = current frame" took the old frame
    private long CurrentFrame => Math.Max(0, _workspace.CurrentSession?.RequestedFrameIndex ?? 0);

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;

    public void OnNavigatedTo(NavigationContext navigationContext) => Rebuild();

    public void OnNavigatedFrom(NavigationContext navigationContext)
    {
    }

    // background job: follows the region's object frame by frame, the path goes back into the store at the end
    // (on cancel too)
    private void StartTracking()
    {
        if (_selected is null || _workspace.CurrentSession is not { } session)
        {
            return;
        }

        // reads the file on its own decoder, playback isn't touched
        var job = new RegionTrackingJob(session.Metadata.SourcePath, _decoders, _trackers, _store, _selected.Region.Id);
        _trackingJob = job;
        // the job raises these on its worker thread -> Post, they feed bound properties
        job.ProgressChanged += (_, p) => _ui.Post(() => TrackingStatus = p.Message);
        job.StateChanged += (_, e) => _ui.Post(() =>
        {
            RaiseTrackingState();
            TrackingStatus = e.Current switch
            {
                ProcessingJobState.Completed when job.TargetLost => $"Target lost after frame {job.LastTrackedFrame + 1:N0}: region trimmed there.",
                ProcessingJobState.Completed => $"Tracked to frame {job.LastTrackedFrame + 1:N0}. The region now follows the object.",
                ProcessingJobState.Cancelled => "Tracking cancelled; the path tracked so far is kept.",
                ProcessingJobState.Failed => "Tracking failed: " + e.Error?.Message,
                _ => TrackingStatus,
            };
        });
        // runs on the pool. a new video or shutdown cancels it
        _jobs.Start(job);
        TrackingStatus = "Tracking…";
        RaiseTrackingState();
    }

    private void RaiseTrackingState()
    {
        RaisePropertyChanged(nameof(IsTracking));
        TrackCommand.RaiseCanExecuteChanged();
        CancelTrackingCommand.RaiseCanExecuteChanged();
    }

    // the list isn't touched here, the store's Changed -> Rebuild does that
    private void UpdateSelected(Func<RedactionRegion, RedactionRegion> change)
    {
        if (_selected is not null)
        {
            _store.Update(change(_selected.Region));
        }
    }

    // UI thread only
    private void Rebuild()
    {
        var metadata = _workspace.CurrentSession?.Metadata;
        // Clear() and the re-select below set SelectedRegion from code. _syncing keeps that away from Editor
        _syncing = true;
        try
        {
            Regions.Clear();
            var number = 0;
            foreach (var region in _store.Regions.OrderBy(r => r.StartFrame))
            {
                // timecodes need the fps. w/o a video: 1-based frame numbers
                var range = metadata is null
                    ? $"frames {region.StartFrame + 1}–{region.EndFrame + 1}"
                    : $"{DisplayFormat.Timecode(metadata.TimeOfFrame(region.StartFrame))} – {DisplayFormat.Timecode(metadata.TimeOfFrame(region.EndFrame))}";
                Regions.Add(new RegionItem(region, $"Region {++number} · {region.Effect}{(region.IsTracked ? " · tracked" : string.Empty)}", range));
            }

            // by id, the old RegionItems are gone
            SelectedRegion = Regions.FirstOrDefault(r => r.Region.Id == Editor.SelectedRegionId);
        }
        finally
        {
            _syncing = false;
        }

        // settings may have changed too (e.g. a project was opened)
        RaisePropertyChanged(nameof(DetectionEffect));
        RaisePropertyChanged(nameof(PaddingPercent));
        RaisePropertyChanged(nameof(StrengthPercent));
    }

    // selection changed elsewhere (e.g. a click on the video): select the matching row
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RedactionEditorState.SelectedRegionId))
        {
            _syncing = true;
            SelectedRegion = Regions.FirstOrDefault(r => r.Region.Id == Editor.SelectedRegionId);
            _syncing = false;
        }
    }
}
