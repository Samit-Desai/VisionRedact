using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using Prism.Commands;
using Prism.Events;
using VisionRedact.Application.Detections;
using VisionRedact.Application.People;
using VisionRedact.Application.Playback;
using VisionRedact.Application.Redaction;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.People;
using VisionRedact.Domain.Redaction;
using VisionRedact.Presentation.Wpf.Controls;
using VisionRedact.Presentation.Wpf.Events;
using VisionRedact.Presentation.Wpf.Features.Detections;
using VisionRedact.Presentation.Wpf.Features.Redaction;
using VisionRedact.Presentation.Wpf.Infrastructure;

namespace VisionRedact.Presentation.Wpf.Features.Player;

// the boxes on the video, the timeline marks, region editing and "detect this frame" (D).
// nothing per box or per frame comes here from background threads: the boxes are rebuilt when a frame is drawn
// (UI thread) and on a 2x-a-second check of the repository's Version, the markers on that same tick when Version moved.
// boxes stay normalized, VideoSurface maps them w/ the same letterbox it uses for the mouse (ADR-0012)
public sealed partial class PlayerViewModel
{
    // a new region covers 5 s from the current frame, adjusted in the Redaction tab
    private static readonly TimeSpan DefaultRegionDuration = TimeSpan.FromSeconds(5);

    private IDetectionRepository _detectionRepository = null!;
    private IPeopleStore _people = null!;
    private IRedactionRegionStore _regionStore = null!;
    private FrameDetectionService _frameDetection = null!;
    private DetectionPreferences _preferences = null!;
    // all set in InitializeOverlays, from the ctor
    private DispatcherTimer _overlayTimer = null!;
    // what the current boxes were built for, -1 = never. Version moves on every repository change,
    // so comparing it is a cheap "anything new?"
    private long _overlayFrame = -1;
    private long _overlayVersion = -1;
    private long _markerVersion = -1;
    // ids + redact flags of the paused frame's boxes: tells when the paused preview needs a redraw
    private int _displayedSignature;
    // regions or the selection changed: rebuild boxes and timeline ranges
    private bool _regionsDirty = true;
    private Guid? _selectedDetectionId;
    private bool _showDetections = true;
    private string _toolStatus = string.Empty;

    // shared w/ the Redaction tab
    public RedactionEditorState Editor { get; private set; } = null!;

    // VideoSurface finished drawing a new box
    public DelegateCommand<RegionEdit> RegionDrawnCommand { get; private set; } = null!;

    // moved or resized on the video
    public DelegateCommand<RegionEdit> RegionChangedCommand { get; private set; } = null!;

    // clicked on the video: its id, or null to clear
    public DelegateCommand<object> RegionSelectedCommand { get; private set; } = null!;

    // Delete key
    public DelegateCommand DeleteSelectedRegionCommand { get; private set; } = null!;

    // D key / button: the models on this frame only
    public AsyncCommand DetectFrameCommand { get; private set; } = null!;

    public bool ShowDetections
    {
        get => _showDetections;
        set => SetProperty(ref _showDetections, value);
    }

    // the status line under the player
    public string ToolStatus
    {
        get => _toolStatus;
        private set => SetProperty(ref _toolStatus, value);
    }

    // the overlay half of the ctor
    private void InitializeOverlays(
        IDetectionRepository detections,
        IRedactionRegionStore regions,
        RedactionEditorState editor,
        FrameDetectionService frameDetection,
        DetectionPreferences preferences,
        IPeopleStore people)
    {
        _detectionRepository = detections;
        _people = people;
        _regionStore = regions;
        _frameDetection = frameDetection;
        _preferences = preferences;
        Editor = editor;

        RegionDrawnCommand = new DelegateCommand<RegionEdit>(OnRegionDrawn);
        // store a copy w/ the new box
        RegionChangedCommand = new DelegateCommand<RegionEdit>(e =>
        {
            if (e.RegionId is { } id && _regionStore.Regions.FirstOrDefault(r => r.Id == id) is { } region)
            {
                _regionStore.Update(region with { Box = e.Box });
            }
        });
        RegionSelectedCommand = new DelegateCommand<object>(id => Editor.SelectedRegionId = id as Guid?);
        DeleteSelectedRegionCommand = new DelegateCommand(() =>
        {
            if (Editor.SelectedRegionId is { } id)
            {
                _regionStore.Remove(id);
                Editor.SelectedRegionId = null;
            }
        });
        // errors go to the status line. only w/ a video
        DetectFrameCommand = new AsyncCommand(DetectFrameAsync, ex => ToolStatus = ex.Message, () => _session is not null);

        // mostly UI-thread edits, but the tracking job writes from its own thread: Post
        _regionStore.Changed += (_, _) => _ui.Post(() =>
        {
            _regionsDirty = true;
            RefreshOverlaysNow();
            RefreshPausedPreview();
        });
        Editor.PropertyChanged += OnEditorChanged;

        // a rename or merge changes the captions. the identify job writes from its own thread
        _people.Changed += (_, _) => _ui.Post(RefreshOverlaysNow);

        // the Detections list selects, the Detections / Redaction / People tabs seek, none of them holds the player
        _events.GetEvent<SelectedDetectionChangedEvent>().Subscribe(OnDetectionSelected, ThreadOption.UIThread);
        _events.GetEvent<SeekRequestedEvent>().Subscribe(SeekTo, ThreadOption.UIThread);

        // UI thread, Background priority: the tick waits behind rendering and input
        _overlayTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => OnOverlayTick(), System.Windows.Application.Current.Dispatcher);
        _overlayTimer.Start();
    }

    // new video: forget everything. -1 forces the next rebuild
    private void ResetOverlays()
    {
        _overlayFrame = -1;
        _overlayVersion = -1;
        _markerVersion = -1;
        _regionsDirty = true;
        _selectedDetectionId = null;
        DetectionBoxes = [];
        RedactionBoxes = [];
        TimelineMarkers = [];
        TimelineRanges = [];
        SelectionStart = SelectionEnd = double.NaN;
        DetectFrameCommand.RaiseCanExecuteChanged();
    }

    // boxes for one frame. per drawn frame from ShowRenderedFrame, w/ force when the data changed but the frame
    // didn't. UI thread only
    private void UpdateOverlays(long frameIndex, bool force = false)
    {
        var version = _detectionRepository.Version;
        if (!force && frameIndex == _overlayFrame && version == _overlayVersion && !_regionsDirty)
        {
            return;
        }

        _overlayFrame = frameIndex;
        _overlayVersion = version;

        var detections = _detectionRepository.GetForDisplay(frameIndex);
        // immutable: read once per frame, no lock
        var people = _people.Current;
        DetectionBoxes = detections.Count == 0
            ? []
            : detections.Select(d => new OverlayBox(d.Id, d.Box, KindOf(d), LabelOf(d, people))).ToArray();

        // regions active here, plus the selected one even outside its range ("inactive here"), so it can still be
        // edited. BoxAt = the tracked position
        var selectedRegion = Editor.SelectedRegionId;
        RedactionBoxes = _regionStore.Regions
            .Where(r => r.IsActiveAt(frameIndex) || r.Id == selectedRegion)
            .Select(r => new OverlayBox(
                r.Id,
                r.BoxAt(frameIndex),
                r.Id == selectedRegion ? OverlayBoxKind.SelectedRedaction : OverlayBoxKind.Redaction,
                r.IsActiveAt(frameIndex) ? EffectLabel(r.Effect) + (r.IsTracked ? " · tracked" : string.Empty) : EffectLabel(r.Effect) + " (inactive here)"))
            .ToArray();

        // ranges don't depend on the frame
        if (_regionsDirty)
        {
            _regionsDirty = false;
            UpdateRanges();
        }
    }

    // same frame, the data changed
    private void RefreshOverlaysNow()
    {
        if (_session is not null)
        {
            UpdateOverlays(Math.Max(0, _session.CurrentFrameIndex), force: true);
        }
    }

    // 2x a second: catches detection changes while paused (scan running, review edits), rebuilds the markers
    private void OnOverlayTick()
    {
        if (_session is null)
        {
            return;
        }

        // e.g. the scan found more since the boxes were built
        var version = _detectionRepository.Version;
        if (version != _overlayVersion)
        {
            RefreshOverlaysNow();
        }

        // re-decode the paused frame only if *its* redactions changed, not on every new box elsewhere.
        // costs one decode, and the preview shows review decisions w/o pressing play
        var signature = Signature(_detectionRepository.GetForDisplay(Math.Max(0, _session.CurrentFrameIndex)));
        if (signature != _displayedSignature)
        {
            _displayedSignature = signature;
            RefreshPausedPreview();
        }

        // markers only when Version moved
        if (version != _markerVersion)
        {
            _markerVersion = version;
            TimelineMarkers = _detectionRepository.Snapshot()
                .Select(d => new TimelineMarker(d.FrameIndex, d.Status switch
                {
                    ReviewStatus.Approved => TimelineMarkerKind.Approved,
                    ReviewStatus.Rejected => TimelineMarkerKind.Rejected,
                    _ => TimelineMarkerKind.Detection,
                }))
                .ToArray();
        }
    }

    private void UpdateRanges()
    {
        var selected = Editor.SelectedRegionId;
        TimelineRanges = _regionStore.Regions
            .Select(r => new TimelineRange(r.StartFrame, r.EndFrame, r.Id == selected ? TimelineRangeKind.SelectedRedaction : TimelineRangeKind.Redaction))
            .ToArray();

        // NaN = no highlight
        var region = _regionStore.Regions.FirstOrDefault(r => r.Id == selected);
        SelectionStart = region?.StartFrame ?? double.NaN;
        SelectionEnd = region?.EndFrame ?? double.NaN;
    }

    // paused + preview on: re-decode the current frame so the blur shows the latest edits.
    // while playing the next frame picks them up anyway
    private void RefreshPausedPreview()
    {
        if (_session is { IsPlayRequested: false } session && Editor.IsPreviewEnabled)
        {
            session.Refresh();
        }
    }

    // a drawn box -> a new region, selected
    private void OnRegionDrawn(RegionEdit edit)
    {
        if (_session is null || _metadata is null)
        {
            return;
        }

        // from the frame on screen (the one drawn on), clamped to the end. same effect as the AI boxes
        var start = Math.Max(0, _session.CurrentFrameIndex);
        var duration = (long)Math.Round(DefaultRegionDuration.TotalSeconds * _metadata.FramesPerSecond);
        var region = RedactionRegion.Create(edit.Box, start, _metadata.ClampFrame(start + duration), _regionStore.Settings.DetectionEffect);
        _regionStore.Add(region);
        // via Editor, so the Redaction tab selects it too
        Editor.SelectedRegionId = region.Id;
        ToolStatus = "Region added for 5 s from this frame — adjust its range in the Redaction panel.";
    }

    // maybe from the Redaction tab. new selection = recolor boxes + ranges.
    // preview toggled while paused = re-decode, so the picture matches
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RedactionEditorState.SelectedRegionId))
        {
            _regionsDirty = true;
            RefreshOverlaysNow();
        }
        else if (e.PropertyName == nameof(RedactionEditorState.IsPreviewEnabled) && _session is not null && !_session.IsPlayRequested)
        {
            _session.Refresh();
        }
    }

    // a row picked in the Detections list: highlight its box, jump to its frame
    private void OnDetectionSelected(Detection? detection)
    {
        _selectedDetectionId = detection?.Id;
        if (detection is not null)
        {
            SeekTo(detection.FrameIndex);
        }

        RefreshOverlaysNow();
    }

    // decode + inference run off the UI thread inside FrameDetectionService; each await comes back on the UI thread
    private async Task DetectFrameAsync()
    {
        if (_session is null)
        {
            return;
        }

        // not loaded yet if the Detections tab was never opened
        if (_preferences.Models.Count == 0)
        {
            await _preferences.RefreshAsync();
        }

        var models = _preferences.SelectedModels;
        if (models.Count == 0)
        {
            ToolStatus = "No detection model installed or selected (Detections panel).";
            return;
        }

        // TODO: the frame on screen here, the Detections tab's button uses RequestedFrameIndex. they only differ
        // during a slow seek, ok for now
        var frame = Math.Max(0, _session.CurrentFrameIndex);
        ToolStatus = $"Detecting on frame {frame + 1:N0}…";
        var found = await _frameDetection.DetectFrameAsync(_session.Metadata.SourcePath, frame, models);
        ToolStatus = $"Frame {frame + 1:N0}: {found.Count} detection(s) with {string.Join(" + ", models.Select(m => m.DisplayName))}.";
        // in the repository now: redraw right away, don't wait for the tick
        RefreshOverlaysNow();
        RefreshPausedPreview();
    }

    // what's redacted on a frame: which boxes, and redacted or not. unchanged = no re-decode of the paused preview
    private static int Signature(IReadOnlyList<Detection> detections)
    {
        var hash = new HashCode();
        foreach (var d in detections)
        {
            hash.Add(d.Id);
            hash.Add(d.IsRedacted);
        }

        return hash.ToHashCode();
    }

    // the color: selected wins, else the review status
    private OverlayBoxKind KindOf(Detection d) =>
        d.Id == _selectedDetectionId ? OverlayBoxKind.SelectedDetection : d.Status switch
        {
            ReviewStatus.Approved => OverlayBoxKind.ApprovedDetection,
            ReviewStatus.Rejected => OverlayBoxKind.RejectedDetection,
            _ => OverlayBoxKind.Detection,
        };

    // caption on screen only, never drawn into an export: the person's name if the track has one,
    // else class + confidence, plus the track id once the tracker linked it
    private static string LabelOf(Detection d, PeopleIndex people) => d.TrackId switch
    {
        { } track when people.PersonOf(track) is { } person => string.Create(CultureInfo.CurrentCulture, $"{person.Name} {d.Confidence:P0}"),
        { } track => string.Create(CultureInfo.CurrentCulture, $"{d.Class} {d.Confidence:P0} · T{track}"),
        null => string.Create(CultureInfo.CurrentCulture, $"{d.Class} {d.Confidence:P0}"),
    };

    private static string EffectLabel(RedactionEffectKind effect) => effect switch
    {
        RedactionEffectKind.GaussianBlur => "Blur",
        RedactionEffectKind.Pixelate => "Pixelate",
        _ => "Black box",
    };
}
