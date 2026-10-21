namespace VisionRedact.Application.Media;

// small Bgr24 picture in managed memory: a timeline tile, a hover preview or a face thumbnail.
// unlike OwnedVideoFrame: no native memory, no owner, the GC frees it, so it can go between threads and be kept.
// ok b/c these are tiny. full frames stay native
public sealed record ImageBuffer
{
    public ImageBuffer(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels.Length, stride * height, nameof(pixels));
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    // row by row. nothing stops a write, so treat it as read-only
    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    // bytes per row, at least Width * 3
    public int Stride { get; }
}
