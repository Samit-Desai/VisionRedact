using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VisionRedact.Presentation.Wpf.Controls;

/// <summary>Lookless video timeline: playhead, scrub, markers, ranges, a selection. Template in Themes/Generic.xaml.</summary>
// trick: PART_Track draws the rare stuff (ticks, ranges, markers) in one OnRender, PART_Playhead is its own element
// moved by a TranslateTransform. so the playhead moving at frame rate is render-only (no layout, no track redraw),
// and the markers are only redrawn when they or the size change.
// all in frames (Maximum = last index), fps is only for the tick labels. knows nothing about sessions or decoders.
// mouse down -> capture + scrub -> Position (two-way) on every new frame -> release / lost capture ends it
// hover: HoverFrameCommand gets the frame under the pointer (only when it changes), null on leave or scrub start.
// the host answers w/ HoverPreview + HoverText, the control never decodes
[TemplatePart(Name = PartTrack, Type = typeof(TimelineTrack))]
[TemplatePart(Name = PartPlayhead, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartHoverPreview, Type = typeof(FrameworkElement))]
public class VideoTimelineControl : Control
{
    public const string PartTrack = "PART_Track";
    public const string PartPlayhead = "PART_Playhead";
    public const string PartHoverPreview = "PART_HoverPreview";

    // OnTrackContentChanged redraws the track whenever something it draws changes
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(VideoTimelineControl),
        new FrameworkPropertyMetadata(0.0, OnTrackContentChanged), IsNonNegative);

    // two-way by default, so a scrub writes back into the view model (its setter seeks).
    // CoercePosition keeps 0..Maximum, OnPositionChanged only moves the playhead
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(VideoTimelineControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged, CoercePosition));

    public static readonly DependencyProperty FramesPerSecondProperty = DependencyProperty.Register(
        nameof(FramesPerSecond), typeof(double), typeof(VideoTimelineControl),
        new FrameworkPropertyMetadata(30.0, OnTrackContentChanged));

    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(double), typeof(VideoTimelineControl),
        new FrameworkPropertyMetadata(double.NaN, OnTrackContentChanged));

    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(double), typeof(VideoTimelineControl),
        new FrameworkPropertyMetadata(double.NaN, OnTrackContentChanged));

    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<TimelineMarker>), typeof(VideoTimelineControl),
        new FrameworkPropertyMetadata(Array.Empty<TimelineMarker>(), OnTrackContentChanged));

    public static readonly DependencyProperty RangesProperty = DependencyProperty.Register(
        nameof(Ranges), typeof(IReadOnlyList<TimelineRange>), typeof(VideoTimelineControl),
        new FrameworkPropertyMetadata(Array.Empty<TimelineRange>(), OnTrackContentChanged));

    public static readonly DependencyProperty ScrubStartedCommandProperty = DependencyProperty.Register(
        nameof(ScrubStartedCommand), typeof(ICommand), typeof(VideoTimelineControl));

    public static readonly DependencyProperty ScrubCompletedCommandProperty = DependencyProperty.Register(
        nameof(ScrubCompletedCommand), typeof(ICommand), typeof(VideoTimelineControl));

    public static readonly DependencyProperty HoverFrameCommandProperty = DependencyProperty.Register(
        nameof(HoverFrameCommand), typeof(ICommand), typeof(VideoTimelineControl));

    public static readonly DependencyProperty HoverPreviewProperty = DependencyProperty.Register(
        nameof(HoverPreview), typeof(ImageSource), typeof(VideoTimelineControl));

    public static readonly DependencyProperty HoverTextProperty = DependencyProperty.Register(
        nameof(HoverText), typeof(string), typeof(VideoTimelineControl));

    // read-only outside, only this class sets it. nothing binds to it right now
    private static readonly DependencyPropertyKey IsScrubbingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsScrubbing), typeof(bool), typeof(VideoTimelineControl), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsScrubbingProperty = IsScrubbingPropertyKey.DependencyProperty;

    // bubbling routed events, for a code-behind host. NOTE: nothing handles any of the three today, PlayerView
    // uses the Position binding and the scrub commands.
    // careful: SeekRequestedEvent here is not the Prism SeekRequestedEvent in Events/ (the "go to frame" buttons)
    public static readonly RoutedEvent SeekRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(SeekRequested), RoutingStrategy.Bubble, typeof(TimelineSeekEventHandler), typeof(VideoTimelineControl));

    public static readonly RoutedEvent ScrubStartedEvent = EventManager.RegisterRoutedEvent(
        nameof(ScrubStarted), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(VideoTimelineControl));

    public static readonly RoutedEvent ScrubCompletedEvent = EventManager.RegisterRoutedEvent(
        nameof(ScrubCompleted), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(VideoTimelineControl));

    // the playhead only ever moves by this X: render-only, no measure / arrange
    private readonly TranslateTransform _playheadTransform = new();
    private readonly TranslateTransform _hoverTransform = new();
    private TimelineTrack? _track;
    private FrameworkElement? _playhead;
    private FrameworkElement? _hoverPreview;
    private long? _hoverFrame; // last frame sent to HoverFrameCommand, null = not hovering
    // last frame sent in this scrub (-1 = none), to skip repeats
    private long _lastSeekFrame = -1;

    // not focusable: it's mouse-driven, a click shouldn't steal keyboard focus
    static VideoTimelineControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(VideoTimelineControl), new FrameworkPropertyMetadata(typeof(VideoTimelineControl)));
        FocusableProperty.OverrideMetadata(typeof(VideoTimelineControl), new FrameworkPropertyMetadata(false));
    }

    public event TimelineSeekEventHandler SeekRequested
    {
        add => AddHandler(SeekRequestedEvent, value);
        remove => RemoveHandler(SeekRequestedEvent, value);
    }

    public event RoutedEventHandler ScrubStarted
    {
        add => AddHandler(ScrubStartedEvent, value);
        remove => RemoveHandler(ScrubStartedEvent, value);
    }

    public event RoutedEventHandler ScrubCompleted
    {
        add => AddHandler(ScrubCompletedEvent, value);
        remove => RemoveHandler(ScrubCompletedEvent, value);
    }

    /// <summary>Last frame index (inclusive).</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Current frame (the playhead), coerced into 0..Maximum.</summary>
    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double FramesPerSecond
    {
        get => (double)GetValue(FramesPerSecondProperty);
        set => SetValue(FramesPerSecondProperty, value);
    }

    /// <summary>First frame of the highlighted range, NaN = none.</summary>
    public double SelectionStart
    {
        get => (double)GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public double SelectionEnd
    {
        get => (double)GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    // set a new list (a snapshot) to redraw, no per-item change tracking
    public IReadOnlyList<TimelineMarker> Markers
    {
        get => (IReadOnlyList<TimelineMarker>)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public IReadOnlyList<TimelineRange> Ranges
    {
        get => (IReadOnlyList<TimelineRange>)GetValue(RangesProperty);
        set => SetValue(RangesProperty, value);
    }

    public ICommand? ScrubStartedCommand
    {
        get => (ICommand?)GetValue(ScrubStartedCommandProperty);
        set => SetValue(ScrubStartedCommandProperty, value);
    }

    public ICommand? ScrubCompletedCommand
    {
        get => (ICommand?)GetValue(ScrubCompletedCommandProperty);
        set => SetValue(ScrubCompletedCommandProperty, value);
    }

    /// <summary>Run with the hovered frame (long) when it changes, and with null when hovering ends.</summary>
    public ICommand? HoverFrameCommand
    {
        get => (ICommand?)GetValue(HoverFrameCommandProperty);
        set => SetValue(HoverFrameCommandProperty, value);
    }

    /// <summary>Picture for PART_HoverPreview. The host supplies it, the control never decodes.</summary>
    public ImageSource? HoverPreview
    {
        get => (ImageSource?)GetValue(HoverPreviewProperty);
        set => SetValue(HoverPreviewProperty, value);
    }

    /// <summary>Label under the hover picture, e.g. the hovered frame's timecode.</summary>
    public string? HoverText
    {
        get => (string?)GetValue(HoverTextProperty);
        set => SetValue(HoverTextProperty, value);
    }

    /// <summary>True while the user drags on the timeline (mouse captured).</summary>
    public bool IsScrubbing => (bool)GetValue(IsScrubbingProperty);

    // a custom Control has no automation peer, so UIA (screen readers, UI tests, the demo recorder) couldn't find it
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    public override void OnApplyTemplate()
    {
        // a template can be applied again (style change): unhook the old track first
        if (_track is not null)
        {
            _track.SizeChanged -= OnTrackSizeChanged;
        }

        base.OnApplyTemplate();
        // parts are optional: a template w/o one just loses that feature
        _track = GetTemplateChild(PartTrack) as TimelineTrack;
        _playhead = GetTemplateChild(PartPlayhead) as FrameworkElement;
        _hoverPreview = GetTemplateChild(PartHoverPreview) as FrameworkElement;

        if (_track is not null)
        {
            _track.Owner = this;
            _track.SizeChanged += OnTrackSizeChanged;
        }

        if (_playhead is not null)
        {
            _playhead.RenderTransform = _playheadTransform;
            // display only, the mouse goes to the timeline
            _playhead.IsHitTestVisible = false;
        }

        if (_hoverPreview is not null)
        {
            _hoverPreview.RenderTransform = _hoverTransform;
            _hoverPreview.IsHitTestVisible = false;
            _hoverPreview.Visibility = Visibility.Hidden; // Hidden, not Collapsed: keeps its layout slot, so show / hide is render-only
        }

        UpdatePlayhead();
    }

    // frame -> x in the track (DIPs)
    public double FrameToX(double frame)
    {
        var width = _track?.ActualWidth ?? ActualWidth;
        return Maximum <= 0 ? 0 : Math.Clamp(frame / Maximum, 0, 1) * width;
    }

    // x in the track (DIPs) -> nearest whole frame
    public double XToFrame(double x)
    {
        var width = _track?.ActualWidth ?? ActualWidth;
        return width <= 0 ? 0 : Math.Round(Math.Clamp(x / width, 0, 1) * Maximum);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled || Maximum <= 0)
        {
            return;
        }

        // capture, so a fast drag past the edge still scrubs to the first / last frame
        if (CaptureMouse())
        {
            EndHover(); // the card is for browsing, while scrubbing the video itself shows the frame
            // scrub starts: flag, routed event + command, then seek to the click
            SetValue(IsScrubbingPropertyKey, true);
            RaiseEvent(new RoutedEventArgs(ScrubStartedEvent, this));
            Execute(ScrubStartedCommand);
            SeekToMouse(e);
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)
        {
            SeekToMouse(e);
        }
        else if (IsEnabled && Maximum > 0)
        {
            UpdateHover(e);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        EndHover();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture(); // -> OnLostMouseCapture ends the scrub
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (IsScrubbing)
        {
            // also when capture is stolen (Alt+Tab, a dialog): the scrub must always end
            SetValue(IsScrubbingPropertyKey, false);
            _lastSeekFrame = -1;
            RaiseEvent(new RoutedEventArgs(ScrubCompletedEvent, this));
            Execute(ScrubCompletedCommand);
        }
    }

    // InvalidateVisual costs an arrange pass. ok here: only when what the track draws changes, never per frame
    internal void InvalidateTrack() => _track?.InvalidateVisual();

    // Maximum: WPF throws on a negative or NaN value
    private static bool IsNonNegative(object value) => value is double d && d >= 0 && !double.IsNaN(d);

    // runs on every set of Position. NaN -> 0
    private static object CoercePosition(DependencyObject d, object value)
    {
        var control = (VideoTimelineControl)d;
        var position = (double)value;
        return double.IsNaN(position) ? 0.0 : Math.Clamp(position, 0, control.Maximum);
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((VideoTimelineControl)d).UpdatePlayhead();

    private static void OnTrackContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (VideoTimelineControl)d;
        // a shorter video can leave Position past the new end: coerce again
        if (e.Property == MaximumProperty)
        {
            control.CoerceValue(PositionProperty);
        }

        control.InvalidateTrack();
        control.UpdatePlayhead();
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlayhead();

    private void UpdatePlayhead()
    {
        if (_playhead is null)
        {
            return;
        }

        // center the line on the frame's x
        _playheadTransform.X = FrameToX(Position) - (_playhead.ActualWidth / 2);
    }

    // mouse x -> frame -> Position (two-way, so the view model seeks) + SeekRequested
    private void SeekToMouse(MouseEventArgs e)
    {
        // relative to the track, that's the area that maps 0..Maximum
        var x = e.GetPosition((IInputElement?)_track ?? this).X;
        var frame = (long)XToFrame(x);
        if (frame == _lastSeekFrame)
        {
            return; // still the same frame, no repeat seek
        }

        _lastSeekFrame = frame;
        Position = frame;
        RaiseEvent(new TimelineSeekEventArgs(SeekRequestedEvent, this, frame));
    }

    // no button down: tell the host the hovered frame (once per frame), move the card w/ the pointer
    private void UpdateHover(MouseEventArgs e)
    {
        var x = e.GetPosition((IInputElement?)_track ?? this).X;
        var frame = (long)XToFrame(x);
        if (frame != _hoverFrame)
        {
            _hoverFrame = frame;
            Execute(HoverFrameCommand, frame);
        }

        if (_hoverPreview is not null)
        {
            // centered on the pointer, kept inside the track
            var trackWidth = _track?.ActualWidth ?? ActualWidth;
            var width = _hoverPreview.ActualWidth;
            _hoverTransform.X = Math.Clamp(x - (width / 2), 0, Math.Max(0, trackWidth - width));
            _hoverPreview.Visibility = Visibility.Visible;
        }
    }

    private void EndHover()
    {
        if (_hoverFrame is null)
        {
            return;
        }

        _hoverFrame = null;
        if (_hoverPreview is not null)
        {
            _hoverPreview.Visibility = Visibility.Hidden;
        }

        Execute(HoverFrameCommand, null);
    }

    // an optional bound command, CanExecute respected
    private static void Execute(ICommand? command, object? parameter = null)
    {
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
        }
    }
}
