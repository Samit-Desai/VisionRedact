namespace VisionRedact.Domain.Geometry;

/// <summary>Box in fractions of the source frame: (0,0) top-left, (1,1) bottom-right.</summary>
// the one box type in the domain (ADR-0012). model box / mouse drag come in, screen px and blur px go out at the edges.
// ctor always clamps to [0,1], so junk model output (NaN, negative, flipped) never reaches the blur
public readonly struct NormalizedRect : IEquatable<NormalizedRect>
{
    public NormalizedRect(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            X = Y = Width = Height = 0;
            return;
        }

        // Min/Max first so a flipped box (negative w/h) comes out normal
        var left = Math.Clamp(Math.Min(x, x + width), 0, 1);
        var top = Math.Clamp(Math.Min(y, y + height), 0, 1);
        var right = Math.Clamp(Math.Max(x, x + width), 0, 1);
        var bottom = Math.Clamp(Math.Max(y, y + height), 0, 1);
        X = left;
        Y = top;
        Width = right - left;
        Height = bottom - top;
    }

    public static NormalizedRect Empty => default;

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double Area => Width * Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PointD Center => new(X + (Width / 2), Y + (Height / 2));

    public static NormalizedRect FromCorners(double x1, double y1, double x2, double y2) => new(x1, y1, x2 - x1, y2 - y1);

    public static NormalizedRect FromPixels(double x, double y, double width, double height, int frameWidth, int frameHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);
        return new NormalizedRect(x / frameWidth, y / frameHeight, width / frameWidth, height / frameHeight);
    }

    public PixelRect ToPixels(int frameWidth, int frameHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);
        if (IsEmpty)
        {
            return default;
        }

        // epsilon: otherwise 0.15000000000000002 * 1000 rounds up to 151
        const double epsilon = 1e-9;
        // round outward - extra blurred px is ok, a missed px is a leak
        var left = (int)Math.Floor((X * frameWidth) + epsilon);
        var top = (int)Math.Floor((Y * frameHeight) + epsilon);
        var right = (int)Math.Ceiling((Right * frameWidth) - epsilon);
        var bottom = (int)Math.Ceiling((Bottom * frameHeight) - epsilon);
        return new PixelRect(left, top, right - left, bottom - top).ClampTo(frameWidth, frameHeight);
    }

    // padding: each side grows by fraction * size
    public NormalizedRect Inflate(double fraction)
    {
        if (IsEmpty)
        {
            return this;
        }

        var dx = Width * fraction;
        var dy = Height * fraction;
        return new NormalizedRect(X - dx, Y - dy, Width + (2 * dx), Height + (2 * dy));
    }

    // t = 0 -> from, t = 1 -> to. slides a tracked box between two scanned frames.
    // NOTE: boxes used to stand still until the next scan, so a moving face slid out of its blur.
    // sliding them along the track worked great (docs/Performance.md, optimization 5)
    public static NormalizedRect Lerp(NormalizedRect from, NormalizedRect to, double t)
    {
        t = double.IsFinite(t) ? Math.Clamp(t, 0, 1) : 0;
        return new NormalizedRect(
            from.X + ((to.X - from.X) * t),
            from.Y + ((to.Y - from.Y) * t),
            from.Width + ((to.Width - from.Width) * t),
            from.Height + ((to.Height - from.Height) * t));
    }

    public NormalizedRect Offset(double dx, double dy)
    {
        // keep the size: at the edge push it back inside, don't shrink it
        var x = Math.Clamp(X + dx, 0, 1 - Width);
        var y = Math.Clamp(Y + dy, 0, 1 - Height);
        return new NormalizedRect(x, y, Width, Height);
    }

    public NormalizedRect Intersect(NormalizedRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? Empty : FromCorners(left, top, right, bottom);
    }

    /// <summary>IoU, 0..1. Used by NMS and the tracker.</summary>
    public double IntersectionOverUnion(NormalizedRect other)
    {
        var intersection = Intersect(other).Area;
        if (intersection <= 0)
        {
            return 0;
        }

        var union = Area + other.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    public bool Contains(PointD point) => point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

    public bool Equals(NormalizedRect other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is NormalizedRect other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public static bool operator ==(NormalizedRect left, NormalizedRect right) => left.Equals(right);

    public static bool operator !=(NormalizedRect left, NormalizedRect right) => !left.Equals(right);

    public override string ToString() => FormattableString.Invariant($"[{X:0.####}, {Y:0.####}, {Width:0.####} × {Height:0.####}]");
}
