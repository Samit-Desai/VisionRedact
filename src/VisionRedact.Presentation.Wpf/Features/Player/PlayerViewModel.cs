using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using VisionRedact.Application.Detections;
using VisionRedact.Application.People;
using VisionRedact.Application.Playback;
using VisionRedact.Application.Preview;
using VisionRedact.Application.Redaction;
using VisionRedact.Domain.Media;
using VisionRedact.Presentation.Wpf.Controls;
using VisionRedact.Presentation.Wpf.Events;
using VisionRedact.Presentation.Wpf.Features.Detections;
using VisionRedact.Presentation.Wpf.Features.Redaction;
using VisionRedact.Presentation.Wpf.Infrastructure;
using VisionRedact.Presentation.Wpf.Rendering;

namespace VisionRedact.Presentation.Wpf.Features.Player;

// transport + timeline for the open PlaybackSession. never touches pixels:
// present task -> presenter -> WritePixels (UI thread) -> FrameRendered -> ShowRenderedFrame, once per drawn frame.
// while playing, the position shown is the frame really on screen; a seek shows its target right away.
// commands only post to the session's mailbox, so none of them can block the UI thread.
// partial: Overlays.cs = boxes, markers, regions. HoverPreview.cs = the timeline card
public sealed partial class PlayerViewModel : BindableBase, IDisposable
{
    private readonly WriteableBitmapFramePresenter _presenter;
    private readonly IUiThread _ui;
    private readonly ILogger<PlayerViewModel> _logger;
    private readonly SubscriptionToken _videoOpenedToken;
    private readonly IEventAggregator _events;

    // null = no video open
    private PlaybackSession? _session;
    private VideoMetadata? _metadata;
    private PlaybackState _state = PlaybackState.Starting;
    // double b/c the slider is. rounded to a frame when seeking
    private double _positionFrame;
    private string _positionText = "--:--.---";
    private string _durationText = "--:--.---";
    private string _frameText = string.Empty;
    private string? _errorText;
    // scrub = dragging the timeline thumb. remember whether to play again after
    private bool _isScrubbing;
    private bool _resumeAfterScrub;
    // each update swaps the whole list, so the view never sees a half-changed one
    private IReadOnlyList<OverlayBox> _detectionBoxes = [];
    private IReadOnlyList<OverlayBox> _redactionBoxes = [];
    private IReadOnlyList<TimelineMarker> _timelineMarkers = [];
    private IReadOnlyList<TimelineRange> _timelineRanges = [];
    private double _selectionStart = double.NaN;
    private double _selectionEnd = double.NaN;

    public PlayerViewModel(
        WriteableBitmapFramePresenter presenter,
        IEventAggregator events,
        IUiThread ui,
        IDetectionRepository detections,
        IRedactionRegionStore regions,
        RedactionEditorState editor,
        FrameDetectionService frameDetection,
        DetectionPreferences preferences,
        FramePreviewService previews,
        IPeopleStore people,
        ILogger<PlayerViewModel> logger)
    {
        _presenter = presenter;
        _events = events;
        _ui = ui;
        _logger = logger;

        // off w/o a video or after a fault
        PlayPauseCommand = new DelegateCommand(() => _session?.TogglePlayPause(), CanControl);
        StepForwardCommand = new DelegateCommand(() => _session?.Step(1), CanControl);
        StepBackwardCommand = new DelegateCommand(() => _session?.Step(-1), CanControl);
        GoToStartCommand = new DelegateCommand(() => SeekTo(0), CanControl);
        GoToEndCommand = new DelegateCommand(() => SeekTo(_metadata?.LastFrameIndex ?? 0), CanControl);
        BeginScrubCommand = new DelegateCommand(BeginScrub);
        EndScrubCommand = new DelegateCommand(EndScrub);

        // both on the UI thread. SurfaceChanged = a new bitmap (e.g. another video size), FrameRendered = one was drawn
        _presenter.SurfaceChanged += OnSurfaceChanged;
        _presenter.FrameRendered += OnFrameRendered;
        // UIThread: delivered on the UI thread whoever publishes. token kept for Dispose
        _videoOpenedToken = events.GetEvent<VideoOpenedEvent>().Subscribe(Attach, ThreadOption.UIThread);
        InitializeOverlays(detections, regions, editor, frameDetection, preferences, people);
        InitializeHoverPreview(previews);
    }

    public DelegateCommand PlayPauseCommand { get; }

    public DelegateCommand StepForwardCommand { get; }

    public DelegateCommand StepBackwardCommand { get; }

    public DelegateCommand GoToStartCommand { get; }

    public DelegateCommand GoToEndCommand { get; }

    public DelegateCommand BeginScrubCommand { get; }

    public DelegateCommand EndScrubCommand { get; }

    // the presenter's WriteableBitmap, bound once. frames are drawn into the same bitmap, so no property change
    // per frame, only when the surface is replaced (ADR-0011)
    public ImageSource? Frame => _presenter.Surface;

    // source size in px, the overlay needs it for the letterbox
    public int VideoWidth => _metadata?.Width ?? 0;

    public int VideoHeight => _metadata?.Height ?? 0;

    public double FramesPerSecond => _metadata?.FramesPerSecond ?? 30;

    // AI boxes on the drawn frame, normalized
    public IReadOnlyList<OverlayBox> DetectionBoxes
    {
        get => _detectionBoxes;
        set => SetProperty(ref _detectionBoxes, value);
    }

    // regions active on the drawn frame, plus the selected one
    public IReadOnlyList<OverlayBox> RedactionBoxes
    {
        get => _redactionBoxes;
        set => SetProperty(ref _redactionBoxes, value);
    }

    // a tick per detection, colored by review status
    public IReadOnlyList<TimelineMarker> TimelineMarkers
    {
        get => _timelineMarkers;
        set => SetProperty(ref _timelineMarkers, value);
    }

    // a bar per manual region
    public IReadOnlyList<TimelineRange> TimelineRanges
    {
        get => _timelineRanges;
        set => SetProperty(ref _timelineRanges, value);
    }

    // highlighted range on the timeline, e.g. the selected region. NaN = none
    public double SelectionStart
    {
        get => _selectionStart;
        set => SetProperty(ref _selectionStart, value);
    }

    public double SelectionEnd
    {
        get => _selectionEnd;
        set => SetProperty(ref _selectionEnd, value);
    }

    public bool HasVideo => _session is not null;

    public bool ShowPlaceholder => !HasVideo;

    public bool IsPlaying => _state == PlaybackState.Playing;

    // Segoe Fluent Icons
    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";

    public string PlayPauseToolTip => IsPlaying ? "Pause (Space)" : "Play (Space)";

    public double MaximumFrame => Math.Max(0, _metadata?.LastFrameIndex ?? 0);

    // two-way bound to the timeline.
    // careful: the setter seeks (user intent). playback writes the field in ShowRenderedFrame instead,
    // or every drawn frame would seek to itself
    public double PositionFrame
    {
        get => _positionFrame;
        set
        {
            if (SetProperty(ref _positionFrame, value))
            {
                UpdatePositionText();
                _session?.Seek((long)Math.Round(value)); // coalesced by the session: scrubbing is cheap
            }
        }
    }

    // e.g. "00:12.345"
    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    // 1-based for people, frame indexes start at 0
    public string FrameText
    {
        get => _frameText;
        private set => SetProperty(ref _frameText, value);
    }

    // shown over the video, null = no error
    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _errorText is not null;

    // thumb drag started: pause, so the decoder only serves the scrub position
    public void BeginScrub()
    {
        if (_session is null || _isScrubbing)
        {
            return;
        }

        _isScrubbing = true;
        _resumeAfterScrub = IsPlaying;
        _session.Pause();
    }

    public void EndScrub()
    {
        if (!_isScrubbing)
        {
            return;
        }

        _isScrubbing = false;
        // once more to the exact spot the thumb was released
        _session?.Seek((long)Math.Round(_positionFrame));
        if (_resumeAfterScrub)
        {
            _session?.Play();
        }
    }

    // unhooks the presenter, VideoOpenedEvent and the hover preview. not the overlay handlers or the overlay timer:
    // the player lives as long as the app
    public void Dispose()
    {
        _presenter.SurfaceChanged -= OnSurfaceChanged;
        _presenter.FrameRendered -= OnFrameRendered;
        _events.GetEvent<VideoOpenedEvent>().Unsubscribe(_videoOpenedToken);
        DisposeHoverPreview();
        Detach();
    }

    // VideoOpenedEvent, UI thread
    private void Attach(PlaybackSession session)
    {
        Detach();
        _session = session;
        _metadata = session.Metadata;
        _session.StateChanged += OnSessionStateChanged;

        ErrorText = null;
        ResetOverlays();
        ResetHoverPreview();
        DurationText = DisplayFormat.Timecode(_metadata.Duration);
        // the field, not PositionFrame: this reset must not seek
        _positionFrame = 0;
        RaisePropertyChanged(nameof(PositionFrame));
        UpdatePositionText();
        ApplyState(session.State);

        RaisePropertyChanged(nameof(HasVideo));
        RaisePropertyChanged(nameof(ShowPlaceholder));
        RaisePropertyChanged(nameof(MaximumFrame));
        RaisePropertyChanged(nameof(VideoWidth));
        RaisePropertyChanged(nameof(VideoHeight));
        RaisePropertyChanged(nameof(FramesPerSecond));
        RaiseCommandsCanExecuteChanged();
    }

    // only stops listening. VideoWorkspace owns and disposes the session
    private void Detach()
    {
        if (_session is not null)
        {
            _session.StateChanged -= OnSessionStateChanged;
        }

        _session = null;
        _isScrubbing = false;
    }

    // raised on a playback task, the only work there is the Post
    private void OnSessionStateChanged(object? sender, PlaybackStateChangedEventArgs e) =>
        _ui.Post(() =>
        {
            if (!ReferenceEquals(sender, _session))
            {
                return; // late event from a session that has since been replaced
            }

            ApplyState(e.Current);
            if (e.Current == PlaybackState.Faulted)
            {
                _logger.LogError(e.Error, "Playback session faulted.");
                ErrorText = "Playback stopped because the video could not be decoded. " + e.Error?.Message;
            }
        });

    private void ApplyState(PlaybackState state)
    {
        _state = state;
        RaisePropertyChanged(nameof(IsPlaying));
        RaisePropertyChanged(nameof(PlayPauseGlyph));
        RaisePropertyChanged(nameof(PlayPauseToolTip));
        RaiseCommandsCanExecuteChanged();
    }

    // new bitmap, the Image must rebind
    private void OnSurfaceChanged(object? sender, EventArgs e) => RaisePropertyChanged(nameof(Frame));

    // already on the UI thread, the presenter raises it right after drawing
    private void OnFrameRendered(object? sender, FrameRenderedEventArgs e) => ShowRenderedFrame(e.FrameIndex);

    // once per frame really drawn: boxes, then timeline + time, in the same UI-thread turn,
    // so picture, boxes and position always match
    private void ShowRenderedFrame(long frameIndex)
    {
        UpdateOverlays(frameIndex);
        if (_isScrubbing)
        {
            return; // the thumb follows the mouse while dragging, not the (older) rendered frame
        }

        // the field, not the setter (that would seek). playhead = a TranslateTransform, time = RenderOnlyText:
        // no layout pass per frame. the time text was throttled once to save layout passes, now it's exact every frame
        _positionFrame = frameIndex;
        RaisePropertyChanged(nameof(PositionFrame));
        UpdatePositionText();
    }

    // through the setter on purpose, so it seeks like a drag
    private void SeekTo(long frameIndex) => PositionFrame = _metadata?.ClampFrame(frameIndex) ?? 0;

    private void UpdatePositionText()
    {
        if (_metadata is null)
        {
            return;
        }

        var frame = _metadata.ClampFrame((long)Math.Round(_positionFrame));
        PositionText = DisplayFormat.Timecode(_metadata.TimeOfFrame(frame));
        FrameText = $"Frame {frame + 1:N0} / {_metadata.FrameCount:N0}";
    }

    private bool CanControl() => _session is not null && _state != PlaybackState.Faulted;

    private void RaiseCommandsCanExecuteChanged()
    {
        PlayPauseCommand.RaiseCanExecuteChanged();
        StepForwardCommand.RaiseCanExecuteChanged();
        StepBackwardCommand.RaiseCanExecuteChanged();
        GoToStartCommand.RaiseCanExecuteChanged();
        GoToEndCommand.RaiseCanExecuteChanged();
    }
}
