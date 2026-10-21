namespace VisionRedact.Domain.Geometry;

// own small geometry types so the domain needs neither WPF nor OpenCV.
// D = double units (model input px, DIPs...), PixelRect = whole pixels

public readonly record struct SizeD(double Width, double Height)
{
    // !(x > 0) so NaN counts as empty too (x <= 0 is false for NaN)
    public bool IsEmpty => !(Width > 0) || !(Height > 0);
}

public readonly record struct PointD(double X, double Y);

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IsEmpty => !(Width > 0) || !(Height > 0);

    // corners in any order (model output boxes come as x1, y1, x2, y2)
    public static RectD FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
}

// the only thing the blur code ever gets: RedactionPlanner turns each box into one with ToPixels,
// clamped to the frame, and the effect only touches those px
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    // long, so a huge frame can't overflow int
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    // empty when it's fully outside the frame
    public PixelRect ClampTo(int frameWidth, int frameHeight)
    {
        var left = Math.Clamp(X, 0, frameWidth);
        var top = Math.Clamp(Y, 0, frameHeight);
        var right = Math.Clamp(Right, 0, frameWidth);
        var bottom = Math.Clamp(Bottom, 0, frameHeight);
        return right <= left || bottom <= top ? default : new PixelRect(left, top, right - left, bottom - top);
    }
}
