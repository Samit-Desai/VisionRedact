using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VisionRedact.Presentation.Wpf.Controls;

// the painted strip of VideoTimelineControl (PART_Track): ticks, ranges, markers. no children, all in OnRender.
// one Rectangle per detection would be tens of thousands of visuals to measure and hit-test. here markers go into
// px columns and are drawn as runs, so the cost is set by the width, not the count.
// WPF keeps the drawing: redrawn only when the data or the size changes, never per video frame
public sealed class TimelineTrack : FrameworkElement
{
    private const double TrackHeight = 14;
    private const double TickAreaHeight = 16;
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x33, 0x3B)));
    private static readonly Brush AnalyzedBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x3C, 0x99, 0xD8)));
    private static readonly Brush RedactionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xE5, 0x48, 0x4D)));
    private static readonly Brush SelectedRedactionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7E)));
    private static readonly Brush DetectionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xA9, 0x3B)));
    private static readonly Brush ApprovedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xC8, 0x7A)));
    private static readonly Brush RejectedBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0x9A, 0x9E, 0xA8)));
    private static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TickTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));
    private static readonly Pen TickPen = Freeze(new Pen(TickTextBrush, 1));
    private static readonly Typeface TickTypeface = new("Segoe UI");

    // seconds between labeled ticks. DrawTicks takes the smallest that keeps the labels apart
    private static readonly double[] TickIntervals = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

    // one byte per px column: the strongest marker kind in it. reused, no garbage per render
    private byte[] _columnKinds = [];

    // set by VideoTimelineControl.OnApplyTemplate, all the data comes from it
    public VideoTimelineControl? Owner { get; set; }

    // no width (the parent stretches it), a fixed height
    protected override Size MeasureOverride(Size availableSize) => new(0, TrackHeight + TickAreaHeight);

    // painter's order, later layers on top
    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0 || Owner is null)
        {
            return;
        }

        // background bar first, the tick labels sit above it
        var trackTop = TickAreaHeight;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, trackTop, width, TrackHeight), 3, 3);

        var maximum = Owner.Maximum;
        if (maximum <= 0)
        {
            return;
        }

        DrawTicks(dc, width, maximum);
        DrawRanges(dc, width, maximum, trackTop);
        DrawMarkers(dc, width, maximum, trackTop);
        DrawSelection(dc, width, maximum, trackTop);
    }

    // new width moves every x
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    // time labels along the top, the smallest interval whose labels don't crowd
    private void DrawTicks(DrawingContext dc, double width, double maximum)
    {
        var fps = Owner!.FramesPerSecond > 0 ? Owner.FramesPerSecond : 30;
        var durationSeconds = (maximum + 1) / fps;
        var interval = TickIntervals.FirstOrDefault(i => width / (durationSeconds / i) >= 80, TickIntervals[^1]);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip; // crisp text at any DPI

        for (var t = 0.0; t <= durationSeconds; t += interval)
        {
            var x = Math.Round(t * fps / maximum * width) + 0.5; // + 0.5 keeps a 1px line sharp
            dc.DrawLine(TickPen, new Point(x, TickAreaHeight - 4), new Point(x, TickAreaHeight));
            var label = TimeSpan.FromSeconds(t).ToString(t >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TickTypeface, 10, TickTextBrush, pixelsPerDip);
            // skip a label that would be cut off at the right edge
            if (x + 2 + text.Width <= width)
            {
                dc.DrawText(text, new Point(x + 2, 0));
            }
        }
    }

    // analyzed = full track height, redactions = a thin band at the bottom.
    // End + 1: ranges are inclusive, a one-frame range is still one frame wide
    private void DrawRanges(DrawingContext dc, double width, double maximum, double top)
    {
        foreach (var range in Owner!.Ranges)
        {
            var x1 = Math.Clamp(range.Start / maximum, 0, 1) * width;
            var x2 = Math.Clamp((range.End + 1) / (maximum + 1), 0, 1) * width;
            var brush = range.Kind switch
            {
                TimelineRangeKind.Analyzed => AnalyzedBrush,
                TimelineRangeKind.SelectedRedaction => SelectedRedactionBrush,
                _ => RedactionBrush,
            };

            var isAnalyzed = range.Kind == TimelineRangeKind.Analyzed;
            var rect = isAnalyzed
                ? new Rect(x1, top, Math.Max(1, x2 - x1), TrackHeight)
                : new Rect(x1, top + TrackHeight - 4, Math.Max(2, x2 - x1), 4);
            dc.DrawRectangle(brush, null, rect);
        }
    }

    // pass 1: each marker into its px column, the strongest kind wins (0 = none).
    // pass 2: one rectangle per run of equal columns
    private void DrawMarkers(DrawingContext dc, double width, double maximum, double top)
    {
        var markers = Owner!.Markers;
        if (markers.Count == 0)
        {
            return;
        }

        var columns = (int)Math.Ceiling(width);
        // grow only when the track got wider, else just zero the part in use
        if (_columnKinds.Length < columns)
        {
            _columnKinds = new byte[columns];
        }
        else
        {
            Array.Clear(_columnKinds, 0, columns);
        }

        for (var i = 0; i < markers.Count; i++)
        {
            var m = markers[i];
            var column = (int)Math.Clamp(m.Frame / maximum * (columns - 1), 0, columns - 1);
            var priority = Priority(m.Kind);
            if (priority > _columnKinds[column])
            {
                _columnKinds[column] = priority;
            }
        }

        var markerTop = top + 2;
        var markerHeight = TrackHeight - 7;
        var runStart = -1;
        byte runKind = 0;
        // one past the end (= none), so the last run is always drawn
        for (var c = 0; c <= columns; c++)
        {
            var kind = c < columns ? _columnKinds[c] : (byte)0;
            if (kind == runKind)
            {
                continue;
            }

            if (runKind != 0)
            {
                dc.DrawRectangle(BrushFor(runKind), null, new Rect(runStart, markerTop, Math.Max(1, c - runStart), markerHeight));
            }

            runStart = c;
            runKind = kind;
        }
    }

    // see-through highlight on the selected range (e.g. the selected region). NaN = none
    private void DrawSelection(DrawingContext dc, double width, double maximum, double top)
    {
        var start = Owner!.SelectionStart;
        var end = Owner.SelectionEnd;
        if (double.IsNaN(start) || double.IsNaN(end))
        {
            return;
        }

        var x1 = Math.Clamp(Math.Min(start, end) / maximum, 0, 1) * width;
        var x2 = Math.Clamp((Math.Max(start, end) + 1) / (maximum + 1), 0, 1) * width;
        dc.DrawRectangle(SelectionBrush, null, new Rect(x1, top - 2, Math.Max(2, x2 - x1), TrackHeight + 4));
    }

    // same column: Approved beats Detection beats Rejected
    private static byte Priority(TimelineMarkerKind kind) => kind switch
    {
        TimelineMarkerKind.Rejected => 1,
        TimelineMarkerKind.Detection => 2,
        TimelineMarkerKind.Approved => 3,
        _ => 1,
    };

    private static Brush BrushFor(byte priority) => priority switch
    {
        1 => RejectedBrush,
        2 => DetectionBrush,
        _ => ApprovedBrush,
    };

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
