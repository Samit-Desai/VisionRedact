using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Presentation.Wpf.Controls;

// picks the color, line width and dash (BoxStyles)
public enum OverlayBoxKind
{
    Detection = 0,
    ApprovedDetection,
    RejectedDetection,
    SelectedDetection,
    Redaction,
    SelectedRedaction,
    EditPreview,
}

/// <summary>A box over the video, normalized to the source frame. Never screen coordinates.</summary>
public sealed record OverlayBox(Guid Id, NormalizedRect Box, OverlayBoxKind Kind, string? Label = null);

// boxes over the Stretch=Uniform frame. shares a Grid cell w/ the Image and redoes the same LetterboxTransform fit,
// so a box lands on the px the Image shows, at any size or DPI (all DIPs, DPI only matters for the label text).
// drawn, not burned into the frame: hiding them never needs a re-decode.
// one element paints every box, no Rectangle per box for WPF to measure, arrange and hit-test
public sealed class BoxOverlay : FrameworkElement
{
    // resize handle on a selected redaction, in DIPs. VideoSurface hit-tests with it too
    public const double HandleSize = 8;

    // set a new list to redraw, editing the old one in place does nothing
    public static readonly DependencyProperty BoxesProperty = DependencyProperty.Register(
        nameof(Boxes), typeof(IReadOnlyList<OverlayBox>), typeof(BoxOverlay),
        new PropertyMetadata(Array.Empty<OverlayBox>(), OnDrawingInputChanged));

    // one extra box on top, e.g. the region being dragged out
    public static readonly DependencyProperty PreviewBoxProperty = DependencyProperty.Register(
        nameof(PreviewBox), typeof(OverlayBox), typeof(BoxOverlay),
        new PropertyMetadata(null, OnDrawingInputChanged));

    // source size in px, to redo the Image's letterbox fit
    public static readonly DependencyProperty VideoWidthProperty = DependencyProperty.Register(
        nameof(VideoWidth), typeof(int), typeof(BoxOverlay), new PropertyMetadata(0, OnDrawingInputChanged));

    public static readonly DependencyProperty VideoHeightProperty = DependencyProperty.Register(
        nameof(VideoHeight), typeof(int), typeof(BoxOverlay), new PropertyMetadata(0, OnDrawingInputChanged));

    // OnRender draws this once. Redraw() re-records it when the boxes change
    private readonly DrawingGroup _drawing = new();

    // pens and brushes made once and frozen, shared by every redraw
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush LabelTextBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Dictionary<OverlayBoxKind, (Pen Pen, Brush Fill, Brush LabelBackground)> BoxStyles = new()
    {
        [OverlayBoxKind.Detection] = CreateStyle(Color.FromRgb(0xF2, 0xA9, 0x3B), 2, 0x18),
        [OverlayBoxKind.ApprovedDetection] = CreateStyle(Color.FromRgb(0x3F, 0xC8, 0x7A), 2, 0x18),
        [OverlayBoxKind.RejectedDetection] = CreateStyle(Color.FromRgb(0x9A, 0x9E, 0xA8), 1, 0x00, dashed: true),
        [OverlayBoxKind.SelectedDetection] = CreateStyle(Color.FromRgb(0x60, 0xE2, 0xFF), 3, 0x28), // cyan highlight
        [OverlayBoxKind.Redaction] = CreateStyle(Color.FromRgb(0xE5, 0x48, 0x4D), 2, 0x20),
        [OverlayBoxKind.SelectedRedaction] = CreateStyle(Color.FromRgb(0xFF, 0x7A, 0x7E), 3, 0x30),
        [OverlayBoxKind.EditPreview] = CreateStyle(Colors.White, 1.5, 0x20, dashed: true),
    };

    public IReadOnlyList<OverlayBox> Boxes
    {
        get => (IReadOnlyList<OverlayBox>)GetValue(BoxesProperty);
        set => SetValue(BoxesProperty, value);
    }

    public OverlayBox? PreviewBox
    {
        get => (OverlayBox?)GetValue(PreviewBoxProperty);
        set => SetValue(PreviewBoxProperty, value);
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

    // the video's fit in this element right now (normalized <-> DIPs)
    public LetterboxTransform Transform => LetterboxTransform.Fit(new SizeD(VideoWidth, VideoHeight), new SizeD(ActualWidth, ActualHeight));

    // only render changes, never layout.
    // good find - AffectsRender isn't render-only: InvalidateVisual calls InvalidateArrange, so the boxes cost a
    // layout pass every frame while playing. re-recording a DrawingGroup that's already on screen updates only
    // that drawing, no measure, no arrange (like the video's px in its bitmap)
    protected override void OnRender(DrawingContext dc) => dc.DrawDrawing(_drawing);

    // new size = new fit. layout is running anyway at this point, so redraw with it
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Redraw();
    }

    private static void OnDrawingInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((BoxOverlay)d).Redraw();

    // Open() throws away the old content
    private void Redraw()
    {
        using var dc = _drawing.Open();
        // no video size or no room yet
        var transform = Transform;
        if (transform.IsEmpty)
        {
            return;
        }

        // only for the text: FormattedText needs the DPI to stay crisp
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var box in Boxes)
        {
            DrawBox(dc, transform, box, pixelsPerDip);
        }

        // last, so the box being edited is on top
        if (PreviewBox is { } preview)
        {
            DrawBox(dc, transform, preview, pixelsPerDip);
        }
    }

    private static void DrawBox(DrawingContext dc, LetterboxTransform transform, OverlayBox box, double pixelsPerDip)
    {
        if (box.Box.IsEmpty)
        {
            return;
        }

        // normalized -> DIPs in this element, bars included
        var r = transform.NormalizedToContainer(box.Box);
        var rect = new Rect(r.X, r.Y, r.Width, r.Height);
        var (pen, fill, labelBackground) = BoxStyles[box.Kind];
        dc.DrawRectangle(fill, pen, rect);

        if (box.Kind is OverlayBoxKind.SelectedRedaction)
        {
            // resize handle, bottom-right
            dc.DrawRectangle(pen.Brush, null, new Rect(rect.Right - (HandleSize / 2), rect.Bottom - (HandleSize / 2), HandleSize, HandleSize));
        }

        if (string.IsNullOrEmpty(box.Label))
        {
            return;
        }

        // small colored tag w/ white text, just above the box if there's room
        var text = new FormattedText(box.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, 11, LabelTextBrush, pixelsPerDip);
        var labelHeight = text.Height + 2;
        var labelY = rect.Top - labelHeight >= 0 ? rect.Top - labelHeight : rect.Top; // else inside, at the top edge
        dc.DrawRectangle(labelBackground, null, new Rect(rect.Left, labelY, text.Width + 8, labelHeight));
        dc.DrawText(text, new Point(rect.Left + 4, labelY + 1));
    }

    // outline pen, faint fill and label background from one color. fillAlpha 0 = no fill
    private static (Pen, Brush, Brush) CreateStyle(Color color, double thickness, byte fillAlpha, bool dashed = false)
    {
        var stroke = Freeze(new SolidColorBrush(color));
        var pen = new Pen(stroke, thickness);
        if (dashed)
        {
            pen.DashStyle = DashStyles.Dash;
        }

        return (Freeze(pen), Freeze(new SolidColorBrush(Color.FromArgb(fillAlpha, color.R, color.G, color.B))), Freeze(new SolidColorBrush(Color.FromArgb(0xD0, color.R, color.G, color.B))));
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
