using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Presentation.Wpf.Controls;

/// <summary>A drawn or edited redaction box, reported to the host.</summary>
public sealed record RegionEdit(Guid? RegionId, NormalizedRect Box);

// the player's video area, layers from the template in Themes/Generic.xaml, bottom to top:
//   PART_Frame (Image of the presenter's WriteableBitmap, Uniform), PART_Detections (AI boxes),
//   PART_Redactions (manual regions + edit preview), PART_Input (transparent: draw / select / move / resize)
// mouse points go through the same LetterboxTransform the overlays draw with, so a drawn box is exactly where the
// export redacts, at any window size, bars or DPI. everything going out is normalized, through commands
[TemplatePart(Name = PartInput, Type = typeof(FrameworkElement))]
public class VideoSurface : Control
{
    public const string PartInput = "PART_Input";
    // a drag thinner than this (w or h, as a fraction of the frame) is a click, not a new region
    private const double MinimumRegionSize = 0.005;

    public static readonly DependencyProperty FrameSourceProperty = DependencyProperty.Register(
        nameof(FrameSource), typeof(ImageSource), typeof(VideoSurface));

    public static readonly DependencyProperty VideoWidthProperty = DependencyProperty.Register(
        nameof(VideoWidth), typeof(int), typeof(VideoSurface), new FrameworkPropertyMetadata(0));

    public static readonly DependencyProperty VideoHeightProperty = DependencyProperty.Register(
        nameof(VideoHeight), typeof(int), typeof(VideoSurface), new FrameworkPropertyMetadata(0));

    public static readonly DependencyProperty DetectionBoxesProperty = DependencyProperty.Register(
        nameof(DetectionBoxes), typeof(IReadOnlyList<OverlayBox>), typeof(VideoSurface), new FrameworkPropertyMetadata(Array.Empty<OverlayBox>()));

    public static readonly DependencyProperty RedactionBoxesProperty = DependencyProperty.Register(
        nameof(RedactionBoxes), typeof(IReadOnlyList<OverlayBox>), typeof(VideoSurface), new FrameworkPropertyMetadata(Array.Empty<OverlayBox>()));

    public static readonly DependencyProperty ShowDetectionsProperty = DependencyProperty.Register(
        nameof(ShowDetections), typeof(bool), typeof(VideoSurface), new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty IsDrawingEnabledProperty = DependencyProperty.Register(
        nameof(IsDrawingEnabled), typeof(bool), typeof(VideoSurface), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty RegionDrawnCommandProperty = DependencyProperty.Register(
        nameof(RegionDrawnCommand), typeof(ICommand), typeof(VideoSurface));

    public static readonly DependencyProperty RegionChangedCommandProperty = DependencyProperty.Register(
        nameof(RegionChangedCommand), typeof(ICommand), typeof(VideoSurface));

    public static readonly DependencyProperty RegionSelectedCommandProperty = DependencyProperty.Register(
        nameof(RegionSelectedCommand), typeof(ICommand), typeof(VideoSurface));

    // read-only outside, the template binds to it
    private static readonly DependencyPropertyKey EditPreviewPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(EditPreview), typeof(OverlayBox), typeof(VideoSurface), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty EditPreviewProperty = EditPreviewPropertyKey.DependencyProperty;

    // drag state, only valid between mouse down and up (lost capture resets it)
    private FrameworkElement? _input;
    private EditMode _mode;
    private Point _dragStart;
    private OverlayBox? _dragTarget;

    static VideoSurface()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(VideoSurface), new FrameworkPropertyMetadata(typeof(VideoSurface)));
    }

    private enum EditMode
    {
        None,
        Draw,
        Move,
        Resize,
    }

    public ImageSource? FrameSource
    {
        get => (ImageSource?)GetValue(FrameSourceProperty);
        set => SetValue(FrameSourceProperty, value);
    }

    public int VideoWidth
    {
        get => (int)GetValue(VideoWidthProperty);
        set => SetValue(VideoWidthProperty, value);
    }

    public int VideoHeight
    {
        get => (int)GetValue(VideoHeightProperty);
        set => SetValue(VideoHeightProperty, value);
    }

    public IReadOnlyList<OverlayBox> DetectionBoxes
    {
        get => (IReadOnlyList<OverlayBox>)GetValue(DetectionBoxesProperty);
        set => SetValue(DetectionBoxesProperty, value);
    }

    public IReadOnlyList<OverlayBox> RedactionBoxes
    {
        get => (IReadOnlyList<OverlayBox>)GetValue(RedactionBoxesProperty);
        set => SetValue(RedactionBoxesProperty, value);
    }

    public bool ShowDetections
    {
        get => (bool)GetValue(ShowDetectionsProperty);
        set => SetValue(ShowDetectionsProperty, value);
    }

    /// <summary>When true, dragging on empty video area draws a new redaction region.</summary>
    public bool IsDrawingEnabled
    {
        get => (bool)GetValue(IsDrawingEnabledProperty);
        set => SetValue(IsDrawingEnabledProperty, value);
    }

    /// <summary>Executed with a <see cref="RegionEdit"/> (RegionId null) when a new region is drawn.</summary>
    public ICommand? RegionDrawnCommand
    {
        get => (ICommand?)GetValue(RegionDrawnCommandProperty);
        set => SetValue(RegionDrawnCommandProperty, value);
    }

    /// <summary>Executed with a <see cref="RegionEdit"/> when an existing region is moved or resized.</summary>
    public ICommand? RegionChangedCommand
    {
        get => (ICommand?)GetValue(RegionChangedCommandProperty);
        set => SetValue(RegionChangedCommandProperty, value);
    }

    /// <summary>Executed with the region id (Guid) or null when the selection changes.</summary>
    public ICommand? RegionSelectedCommand
    {
        get => (ICommand?)GetValue(RegionSelectedCommandProperty);
        set => SetValue(RegionSelectedCommandProperty, value);
    }

    /// <summary>The dashed box while dragging, null otherwise. The redaction overlay draws it.</summary>
    public OverlayBox? EditPreview => (OverlayBox?)GetValue(EditPreviewProperty);

    // same fit the overlays use, from the input layer's size (same Grid cell)
    private LetterboxTransform Transform =>
        _input is null ? default : LetterboxTransform.Fit(new SizeD(VideoWidth, VideoHeight), new SizeD(_input.ActualWidth, _input.ActualHeight));

    // as in the timeline: no automation peer otherwise, so UIA couldn't find it
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    // template (re)applied: move the mouse handlers to the new input layer
    public override void OnApplyTemplate()
    {
        if (_input is not null)
        {
            _input.MouseLeftButtonDown -= OnInputMouseDown;
            _input.MouseMove -= OnInputMouseMove;
            _input.MouseLeftButtonUp -= OnInputMouseUp;
            _input.LostMouseCapture -= OnInputLostCapture;
        }

        base.OnApplyTemplate();
        _input = GetTemplateChild(PartInput) as FrameworkElement;
        if (_input is not null)
        {
            _input.MouseLeftButtonDown += OnInputMouseDown;
            _input.MouseMove += OnInputMouseMove;
            _input.MouseLeftButtonUp += OnInputMouseUp;
            _input.LostMouseCapture += OnInputLostCapture;
        }
    }

    // decides what the drag does, first match wins: resize (on the handle), select + move (on a box),
    // draw (if enabled), else just deselect
    private void OnInputMouseDown(object sender, MouseButtonEventArgs e)
    {
        var transform = Transform;
        if (transform.IsEmpty || _input is null)
        {
            return;
        }

        // DIPs -> normalized, so hit testing works at any size
        var position = e.GetPosition(_input);
        var normalized = transform.ContainerToNormalized(new PointD(position.X, position.Y));
        var hit = HitTestRegion(normalized);
        var selected = RedactionBoxes.FirstOrDefault(b => b.Kind == OverlayBoxKind.SelectedRedaction);

        if (selected is not null && IsOnResizeHandle(transform, selected, position))
        {
            Begin(EditMode.Resize, position, selected);
        }
        else if (hit is not null)
        {
            Execute(RegionSelectedCommand, hit.Id);
            Begin(EditMode.Move, position, hit);
        }
        else if (IsDrawingEnabled)
        {
            Begin(EditMode.Draw, position, null);
        }
        else
        {
            Execute(RegionSelectedCommand, null);
            return;
        }

        // keep the mouse events if the pointer leaves the video mid-drag
        _input.CaptureMouse();
        e.Handled = true;
    }

    private void OnInputMouseMove(object sender, MouseEventArgs e)
    {
        if (_input is null)
        {
            return;
        }

        var position = e.GetPosition(_input);
        // not dragging: only the cursor hint
        if (_mode == EditMode.None)
        {
            UpdateCursor(position);
            return;
        }

        // dragging: live preview box, nothing is committed until mouse up
        SetValue(EditPreviewPropertyKey, ComputeEdit(position) is { } box ? new OverlayBox(Guid.Empty, box, OverlayBoxKind.EditPreview) : null);
    }

    private void OnInputMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == EditMode.None || _input is null)
        {
            return;
        }

        // read the result and the drag state first, releasing capture resets them
        var box = ComputeEdit(e.GetPosition(_input));
        var mode = _mode;
        var target = _dragTarget;
        _input.ReleaseMouseCapture(); // -> LostMouseCapture resets the state

        if (box is not { } result || result.Width < MinimumRegionSize || result.Height < MinimumRegionSize)
        {
            return; // a click, not a drag
        }

        // commit through the command, the control never stores regions
        if (mode == EditMode.Draw)
        {
            Execute(RegionDrawnCommand, new RegionEdit(null, result));
        }
        else if (target is not null && result != target.Box)
        {
            Execute(RegionChangedCommand, new RegionEdit(target.Id, result));
        }
    }

    // after a normal release and when capture is stolen (Alt+Tab, a dialog): always end the drag
    private void OnInputLostCapture(object sender, MouseEventArgs e)
    {
        _mode = EditMode.None;
        _dragTarget = null;
        SetValue(EditPreviewPropertyKey, null);
    }

    private void Begin(EditMode mode, Point position, OverlayBox? target)
    {
        _mode = mode;
        _dragStart = position;
        _dragTarget = target;
    }

    // Draw = start..now corners, Move = the box shifted by the drag (size kept), Resize = top-left stays put
    private NormalizedRect? ComputeEdit(Point position)
    {
        var transform = Transform;
        if (transform.IsEmpty)
        {
            return null;
        }

        var start = transform.ContainerToNormalized(new PointD(_dragStart.X, _dragStart.Y));
        var current = transform.ContainerToNormalized(new PointD(position.X, position.Y));
        return _mode switch
        {
            EditMode.Draw => NormalizedRect.FromCorners(start.X, start.Y, current.X, current.Y),
            EditMode.Move when _dragTarget is not null => _dragTarget.Box.Offset(current.X - start.X, current.Y - start.Y),
            EditMode.Resize when _dragTarget is not null => NormalizedRect.FromCorners(_dragTarget.Box.X, _dragTarget.Box.Y, current.X, current.Y),
            _ => null,
        };
    }

    // own hit test in normalized math: BoxOverlay paints the boxes, they're not elements WPF could hit-test
    private OverlayBox? HitTestRegion(PointD normalized)
    {
        // last drawn wins, but the selected region beats the others where they overlap
        OverlayBox? hit = null;
        foreach (var box in RedactionBoxes)
        {
            if (box.Box.Contains(normalized) && (hit is null || box.Kind == OverlayBoxKind.SelectedRedaction || hit.Kind != OverlayBoxKind.SelectedRedaction))
            {
                hit = box;
            }
        }

        return hit;
    }

    // in DIPs, not normalized, so the handle is the same grab size at any video resolution
    private static bool IsOnResizeHandle(LetterboxTransform transform, OverlayBox box, Point position)
    {
        var r = transform.NormalizedToContainer(box.Box);
        return Math.Abs(position.X - r.Right) <= BoxOverlay.HandleSize && Math.Abs(position.Y - r.Bottom) <= BoxOverlay.HandleSize;
    }

    // diagonal arrows on the handle, four-way over a region, crosshair when drawing
    private void UpdateCursor(Point position)
    {
        if (_input is null)
        {
            return;
        }

        var transform = Transform;
        var selected = RedactionBoxes.FirstOrDefault(b => b.Kind == OverlayBoxKind.SelectedRedaction);
        if (!transform.IsEmpty && selected is not null && IsOnResizeHandle(transform, selected, position))
        {
            _input.Cursor = Cursors.SizeNWSE;
        }
        else if (!transform.IsEmpty && HitTestRegion(transform.ContainerToNormalized(new PointD(position.X, position.Y))) is not null)
        {
            _input.Cursor = Cursors.SizeAll;
        }
        else
        {
            _input.Cursor = IsDrawingEnabled ? Cursors.Cross : null;
        }
    }

    private static void Execute(ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
        }
    }
}
