using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace VisionRedact.Presentation.Wpf.Controls;

/// <summary>A label that can change many times a second w/o a layout pass. Its width comes from the parent, never the text.</summary>
// why not a TextBlock: Text is AffectsMeasure, so the time and frame labels cost a layout pass every frame while
// playing, and in Diagnostics a value that got wider re-measured its row and the whole list (ETW trace).
// same trick as BoxOverlay: draw into a retained DrawingGroup and re-record it, never InvalidateVisual.
// change what's drawn, never the tree or its size.
// with TextWrapping a long value still wraps at the parent's width. only a change of height asks for layout
public sealed class RenderOnlyText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RenderOnlyText), new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(RenderOnlyText), new PropertyMetadata(Brushes.White, OnTextChanged));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(RenderOnlyText), new PropertyMetadata(12.0, OnTextChanged));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(RenderOnlyText), new PropertyMetadata(new FontFamily("Segoe UI"), OnTextChanged));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(RenderOnlyText), new PropertyMetadata(FontWeights.Normal, OnTextChanged));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(RenderOnlyText), new PropertyMetadata(TextAlignment.Left, OnTextChanged));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(RenderOnlyText), new PropertyMetadata(TextWrapping.NoWrap, OnTextChanged));

    private readonly DrawingGroup _drawing = new();
    // the width the last measure got, and the height the text needed at it. a new value w/ the same height fits
    // the space already laid out, so it's only redrawn
    private double _laidOutWidth = double.PositiveInfinity;
    private double _laidOutHeight;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    // take all the width offered, so a longer value never changes the desired width.
    // only an unbounded parent (not what this is for) gets the text's own width
    protected override Size MeasureOverride(Size availableSize)
    {
        _laidOutWidth = availableSize.Width;
        var text = Format();
        _laidOutHeight = text?.Height ?? 0;
        var width = double.IsInfinity(availableSize.Width) ? text?.WidthIncludingTrailingWhitespace ?? 0 : availableSize.Width;
        return new Size(width, _laidOutHeight);
    }

    protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawDrawing(_drawing);

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

    // so screen readers and UIA can read Text, like from a TextBlock
    protected override AutomationPeer OnCreateAutomationPeer() => new RenderOnlyTextAutomationPeer(this);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var label = (RenderOnlyText)d;
        var height = label.Redraw();
        if (label.IsMeasureValid && height != label._laidOutHeight)
        {
            label.InvalidateMeasure();
        }
    }

    // Open() replaces the drawing, the element itself isn't invalidated.
    // returns the text's height, so a change of height can ask for a layout pass
    private double Redraw()
    {
        using var dc = _drawing.Open();
        var text = Format();
        if (text is null)
        {
            return 0;
        }

        var x = 0.0;
        if (TextWrapping == TextWrapping.NoWrap && RenderSize.Width > 0)
        {
            var spare = RenderSize.Width - text.WidthIncludingTrailingWhitespace;
            x = TextAlignment switch
            {
                TextAlignment.Right => spare,
                TextAlignment.Center => spare / 2,
                _ => 0,
            };
        }

        dc.DrawText(text, new Point(x, 0));
        return text.Height;
    }

    // wrapped text is laid out at the measured width, FormattedText aligns each line in it
    private FormattedText? Format()
    {
        if (string.IsNullOrEmpty(Text))
        {
            return null;
        }

        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        var text = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (TextWrapping != TextWrapping.NoWrap && _laidOutWidth > 0 && !double.IsInfinity(_laidOutWidth))
        {
            text.MaxTextWidth = _laidOutWidth;
            text.TextAlignment = TextAlignment;
        }

        return text;
    }

    private sealed class RenderOnlyTextAutomationPeer(RenderOnlyText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(RenderOnlyText);

        protected override string GetNameCore() => ((RenderOnlyText)Owner).Text;
    }
}
