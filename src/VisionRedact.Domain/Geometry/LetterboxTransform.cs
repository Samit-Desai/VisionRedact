namespace VisionRedact.Domain.Geometry;

// letterbox = one uniform scale + centered bars. never stretch, it would distort the faces.
// same math for the model input and for the overlay on screen, so the boxes line up (ADR-0012)
public readonly record struct LetterboxTransform
{
    private LetterboxTransform(SizeD content, SizeD container, double scale, double offsetX, double offsetY)
    {
        Content = content;
        Container = container;
        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public SizeD Content { get; }

    public SizeD Container { get; }

    // container units per content unit
    public double Scale { get; }

    public double OffsetX { get; }

    public double OffsetY { get; }

    // default(LetterboxTransform) has Scale 0 -> the reverse conversions return empty instead of dividing by 0
    public bool IsEmpty => Scale <= 0;

    public RectD ContentRect => new(OffsetX, OffsetY, Content.Width * Scale, Content.Height * Scale);

    public static LetterboxTransform Fit(SizeD content, SizeD container)
    {
        // e.g. the surface isn't measured yet
        if (content.IsEmpty || container.IsEmpty)
        {
            return default;
        }

        var scale = Math.Min(container.Width / content.Width, container.Height / content.Height);
        var offsetX = (container.Width - (content.Width * scale)) / 2;
        var offsetY = (container.Height - (content.Height * scale)) / 2;
        return new LetterboxTransform(content, container, scale, offsetX, offsetY);
    }

    public PointD ContentToContainer(PointD p) => new(OffsetX + (p.X * Scale), OffsetY + (p.Y * Scale));

    public PointD ContainerToContent(PointD p) => IsEmpty ? default : new((p.X - OffsetX) / Scale, (p.Y - OffsetY) / Scale);

    // overlay: normalized -> DIPs on the video surface
    public RectD NormalizedToContainer(NormalizedRect r) => new(
        OffsetX + (r.X * Content.Width * Scale),
        OffsetY + (r.Y * Content.Height * Scale),
        r.Width * Content.Width * Scale,
        r.Height * Content.Height * Scale);

    // model output box (input px) -> normalized
    public NormalizedRect ContainerToNormalized(RectD r)
    {
        if (IsEmpty)
        {
            return NormalizedRect.Empty;
        }

        // no clamp here, the NormalizedRect ctor does it - that's what cuts off anything in the bars
        var w = Content.Width * Scale;
        var h = Content.Height * Scale;
        return new NormalizedRect((r.X - OffsetX) / w, (r.Y - OffsetY) / h, r.Width / w, r.Height / h);
    }

    // mouse point -> normalized.
    // careful: NOT clamped. a point in the bars comes back outside 0..1, that's how callers know it missed the video
    public PointD ContainerToNormalized(PointD p)
    {
        if (IsEmpty)
        {
            return default;
        }

        return new((p.X - OffsetX) / (Content.Width * Scale), (p.Y - OffsetY) / (Content.Height * Scale));
    }
}
