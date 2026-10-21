using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Regions;
using VisionRedact.Application.Detections;
using VisionRedact.Application.People;
using VisionRedact.Application.Workspace;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Jobs;
using VisionRedact.Domain.People;
using VisionRedact.Presentation.Wpf.Events;
using VisionRedact.Presentation.Wpf.Infrastructure;

namespace VisionRedact.Presentation.Wpf.Features.People;

// the People tab: group the face tracks into people, name them, fix the groups, redact or keep a whole person.
// Identify -> IdentificationJob -> IPeopleStore.Apply -> Changed -> Post(Refresh) -> rows.
// every people edit is a function on the immutable index (Apply), so undo/redo come free, and an edit made while
// "Identify people" runs isn't lost (the job applies its groups to the latest index).
// opt-in: grouping by face is biometric, it only starts after the consent box (ADR-0022)
public sealed class PeopleViewModel : BindableBase, INavigationAware
{
    private readonly IPeopleStore _store;
    private readonly PeopleService _service;
    private readonly IDetectionRepository _detections;
    private readonly VideoWorkspace _workspace;
    private readonly FaceThumbnails _thumbnails;
    private readonly IUiThread _ui;
    private readonly IEventAggregator _events;
    private readonly IUserPrompt _prompt;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<PersonId, PersonItemViewModel> _rows = [];

    private PersonItemViewModel? _selectedPerson;
    private PersonItemViewModel? _mergeTarget;
    private string _status = string.Empty;
    private bool _consent;
    private bool _isVisible;
    private long _seenVersion = -1;
    private IdentificationJob? _job;
    private Dictionary<int, TrackFacts> _facts = [];

    public PeopleViewModel(
        IPeopleStore store,
        PeopleService service,
        IDetectionRepository detections,
        VideoWorkspace workspace,
        FaceThumbnails thumbnails,
        IUiThread ui,
        IEventAggregator events,
        IUserPrompt prompt)
    {
        _store = store;
        _service = service;
        _detections = detections;
        _workspace = workspace;
        _thumbnails = thumbnails;
        _ui = ui;
        _events = events;
        _prompt = prompt;

        IdentifyCommand = new DelegateCommand(Identify, CanIdentify);
        CancelIdentifyCommand = new DelegateCommand(() => _job?.Cancel(), () => IsIdentifying);
        UndoCommand = new DelegateCommand(() => _store.Undo(), () => _store.CanUndo);
        RedoCommand = new DelegateCommand(() => _store.Redo(), () => _store.CanRedo);
        RenameCommand = new DelegateCommand<PersonItemViewModel>(p => (p ?? SelectedPerson)?.IsEditing = true);
        ConfirmCommand = new DelegateCommand<PersonItemViewModel>(p => Edit(p, (index, id) => index.Confirm(id)));
        RedactAllCommand = new DelegateCommand<PersonItemViewModel>(RedactAll);
        KeepVisibleCommand = new DelegateCommand<PersonItemViewModel>(KeepVisible);
        MergeCommand = new DelegateCommand(Merge, () => SelectedPerson is not null && MergeTarget is not null && MergeTarget != SelectedPerson);
        MoveToNewPersonCommand = new DelegateCommand<TrackItemViewModel>(t => { if (t is not null) { _store.Apply(i => i.MoveTrack(t.TrackId, null)); } });
        GoToCommand = new DelegateCommand<object>(GoTo);

        // store edits are rare: refresh at once (the identify job writes from its own thread, hence Post).
        // detections change all the time during a scan: a 1 s timer, and only while the tab is shown
        _store.Changed += (_, _) => _ui.Post(Refresh);
        _detections.Changed += (_, e) => { if (e.IsReset) { _ui.Post(Refresh); } };
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshIfDetectionsChanged(), System.Windows.Application.Current.Dispatcher);
    }

    // master / detail as two flat lists, they virtualize better than a tree
    public ObservableCollection<PersonItemViewModel> People { get; } = [];

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = [];

    public PersonItemViewModel? SelectedPerson
    {
        get => _selectedPerson;
        set
        {
            if (SetProperty(ref _selectedPerson, value))
            {
                RaisePropertyChanged(nameof(HasSelectedPerson));
                LoadTracks();
                MergeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedPerson => _selectedPerson is not null;

    // the selected person gets merged into this one
    public PersonItemViewModel? MergeTarget
    {
        get => _mergeTarget;
        set
        {
            if (SetProperty(ref _mergeTarget, value))
            {
                MergeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // the reviewer's OK for biometric face grouping. not saved anywhere
    public bool Consent
    {
        get => _consent;
        set
        {
            if (SetProperty(ref _consent, value))
            {
                IdentifyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ModelsAvailable => _service.IsAvailable;

    public bool IsIdentifying => _job is { } job && !JobStateMachine.IsTerminal(job.State);

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public DelegateCommand IdentifyCommand { get; }

    public DelegateCommand CancelIdentifyCommand { get; }

    public DelegateCommand UndoCommand { get; }

    public DelegateCommand RedoCommand { get; }

    public DelegateCommand<PersonItemViewModel> RenameCommand { get; }

    public DelegateCommand<PersonItemViewModel> ConfirmCommand { get; }

    public DelegateCommand<PersonItemViewModel> RedactAllCommand { get; }

    public DelegateCommand<PersonItemViewModel> KeepVisibleCommand { get; }

    public DelegateCommand MergeCommand { get; }

    public DelegateCommand<TrackItemViewModel> MoveToNewPersonCommand { get; }

    // a person (first appearance) or a track (its first frame)
    public DelegateCommand<object> GoToCommand { get; }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;

    public void OnNavigatedTo(NavigationContext navigationContext)
    {
        _isVisible = true;
        _seenVersion = -1;
        Refresh();
        _timer.Start();
    }

    public void OnNavigatedFrom(NavigationContext navigationContext)
    {
        _isVisible = false;
        _timer.Stop();
    }

    private bool CanIdentify() => Consent && ModelsAvailable && !IsIdentifying && _workspace.CurrentSession is not null && _detections.Count > 0;

    private void Identify()
    {
        if (_workspace.CurrentSession is not { } session)
        {
            return;
        }

        var job = _service.StartIdentification(session.Metadata.SourcePath);
        _job = job;
        job.ProgressChanged += (_, p) => _ui.Post(() => Status = p.Message);
        job.StateChanged += (_, _) => _ui.Post(() => OnJobStateChanged(job));
        OnJobStateChanged(job);
    }

    private void OnJobStateChanged(IdentificationJob job)
    {
        RaisePropertyChanged(nameof(IsIdentifying));
        IdentifyCommand.RaiseCanExecuteChanged();
        CancelIdentifyCommand.RaiseCanExecuteChanged();
        Status = job.State switch
        {
            ProcessingJobState.Completed => $"Grouped {job.IdentifiedTracks} tracks into {job.Groups} people. Check the ones marked \"check\".",
            ProcessingJobState.Failed => $"Identification failed: {job.Error?.Message}",
            ProcessingJobState.Cancelled => "Identification cancelled.",
            _ => "Reading faces…",
        };
    }

    private void Edit(PersonItemViewModel? person, Func<PeopleIndex, PersonId, PeopleIndex> edit)
    {
        if (person is not null && _store.Current.Find(person.Id) is not null)
        {
            _store.Apply(index => index.Find(person.Id) is null ? index : edit(index, person.Id));
        }
    }

    private void Rename(PersonItemViewModel person, string name) => Edit(person, (index, id) => index.Rename(id, name));

    private void Merge()
    {
        if (SelectedPerson is { } source && MergeTarget is { } target)
        {
            _store.Apply(index => index.Find(source.Id) is null || index.Find(target.Id) is null ? index : index.Merge(source.Id, target.Id));
            MergeTarget = null;
        }
    }

    private void RedactAll(PersonItemViewModel? person)
    {
        if (person is not null)
        {
            Status = $"{person.Name}: {_service.RedactAll(person.Id):N0} detections set to redacted.";
        }
    }

    // un-redacting is the risky way: say what will and won't change, and ask first
    private void KeepVisible(PersonItemViewModel? person)
    {
        if (person is null)
        {
            return;
        }

        var (confirmed, suggested) = _service.CountTracks(person.Id);
        if (confirmed == 0)
        {
            Status = $"Confirm {person.Name}'s tracks first: suggested matches are never un-redacted.";
            return;
        }

        var message = $"Leave {person.Name} visible in the export?\n\n{confirmed} confirmed track(s) will no longer be redacted."
            + (suggested > 0 ? $"\n{suggested} suggested track(s) stay redacted until you confirm them." : string.Empty);
        if (_prompt.Confirm("Keep visible", message))
        {
            Status = $"{person.Name}: {_service.KeepVisible(person.Id):N0} detections left visible.";
        }
    }

    private void GoTo(object? target)
    {
        long? frame = target switch
        {
            PersonItemViewModel person => person.FirstFrame,
            TrackItemViewModel track => track.FirstFrame,
            _ => null,
        };
        if (frame is { } f)
        {
            _events.GetEvent<SeekRequestedEvent>().Publish(f);
        }
    }

    private void RefreshIfDetectionsChanged()
    {
        if (_isVisible && _detections.Version != _seenVersion && !_store.Current.IsEmpty)
        {
            Refresh();
        }
    }

    // rows from the store + one detection snapshot, reused by person id
    private void Refresh()
    {
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        IdentifyCommand.RaiseCanExecuteChanged();
        if (!_isVisible)
        {
            return;
        }

        _seenVersion = _detections.Version;
        var index = _store.Current;
        _facts = index.IsEmpty ? [] : TrackFacts.From(_detections.Snapshot());
        var fps = _workspace.CurrentSession?.Metadata.FramesPerSecond ?? 30;

        var persons = index.Persons;
        foreach (var gone in _rows.Keys.Except(persons.Select(p => p.Id)).ToArray())
        {
            People.Remove(_rows[gone]);
            _rows.Remove(gone);
        }

        for (var i = 0; i < persons.Count; i++)
        {
            var person = persons[i];
            if (!_rows.TryGetValue(person.Id, out var row))
            {
                row = new PersonItemViewModel(person.Id, Rename);
                _rows[person.Id] = row;
            }

            UpdateRow(row, person, index, fps);
            var at = People.IndexOf(row);
            if (at < 0)
            {
                People.Insert(i, row);
            }
            else if (at != i)
            {
                People.Move(at, i);
            }
        }

        if (SelectedPerson is not null && !_rows.ContainsKey(SelectedPerson.Id))
        {
            SelectedPerson = null;
        }

        LoadTracks();
        if (index.IsEmpty && !IsIdentifying)
        {
            Status = ModelsAvailable
                ? "Run the AI scan, then \"Identify people\" to group the face tracks into people."
                : "Face models not installed: run scripts/download-face-models.ps1 and rebuild.";
        }
    }

    private void UpdateRow(PersonItemViewModel row, Person person, PeopleIndex index, double fps)
    {
        var assignments = index.Assignments.Where(a => a.PersonId == person.Id).ToArray();
        var facts = assignments.Select(a => _facts.GetValueOrDefault(a.TrackId)).OfType<TrackFacts>().ToArray();
        var seconds = facts.Sum(f => (f.LastFrame - f.FirstFrame + 1) / fps);
        var redaction = facts.Length == 0 ? string.Empty
            : facts.All(f => f.Rejected == 0) ? "Redacted"
            : facts.All(f => f.Rejected == f.Count) ? "Visible in export"
            : "Mixed";
        row.Update(
            person.Name,
            string.Create(CultureInfo.CurrentCulture, $"{assignments.Length} track{(assignments.Length == 1 ? "" : "s")} · {TimeSpan.FromSeconds(seconds):m\\:ss} on screen"),
            redaction,
            assignments.Any(a => !a.IsConfirmed),
            facts.Length == 0 ? 0 : facts.Min(f => f.FirstFrame));

        if (facts.MaxBy(f => f.BestScore)?.Best is { } face)
        {
            _ = row.ShowPictureAsync(face, _thumbnails.GetAsync(face));
        }
    }

    private void LoadTracks()
    {
        Tracks.Clear();
        if (SelectedPerson is not { } person || _workspace.CurrentSession?.Metadata is not { } metadata)
        {
            return;
        }

        foreach (var assignment in _store.Current.Assignments.Where(a => a.PersonId == person.Id).OrderBy(a => _facts.GetValueOrDefault(a.TrackId)?.FirstFrame ?? 0))
        {
            var facts = _facts.GetValueOrDefault(assignment.TrackId);
            var range = facts is null ? "no detections"
                : $"{DisplayFormat.Timecode(metadata.TimeOfFrame(facts.FirstFrame))} – {DisplayFormat.Timecode(metadata.TimeOfFrame(facts.LastFrame))}";
            var item = new TrackItemViewModel(assignment.TrackId, range, assignment.IsConfirmed, facts?.FirstFrame ?? 0);
            Tracks.Add(item);
            if (facts?.Best is { } face)
            {
                _ = item.ShowPictureAsync(_thumbnails.GetAsync(face));
            }
        }
    }

    // per track: time range, review counts, and the best face to show (box area x confidence)
    private sealed record TrackFacts(long FirstFrame, long LastFrame, int Count, int Rejected, Detection Best, double BestScore)
    {
        public static Dictionary<int, TrackFacts> From(IReadOnlyList<Detection> detections) =>
            detections.Where(d => d is { Class: ObjectClass.Face, TrackId: not null })
                .GroupBy(d => d.TrackId!.Value)
                .ToDictionary(g => g.Key, g =>
                {
                    var best = g.MaxBy(Score)!;
                    return new TrackFacts(g.Min(d => d.FrameIndex), g.Max(d => d.FrameIndex), g.Count(), g.Count(d => d.Status == ReviewStatus.Rejected), best, Score(best));
                });

        private static double Score(Detection d) => d.Box.Width * d.Box.Height * d.Confidence;
    }
}
