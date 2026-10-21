namespace VisionRedact.Domain.Media;

// what we know about the open video, plus the frame <-> time math everyone shares.
// EmguVideoDecoderFactory builds it on open; playback, scan, export and the panels all read it.
// careful: assumes a constant frame rate (frame N starts at N / fps). VFR would need per-frame timestamps,
// a known limit of the OpenCV-only stack (ADR-0018)
public sealed record VideoMetadata
{
    public VideoMetadata(
        string sourcePath,
        int width,
        int height,
        double framesPerSecond,
        long frameCount,
        long fileSizeBytes,
        string codec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), framesPerSecond, "Frame rate must be a positive, finite number.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        ArgumentOutOfRangeException.ThrowIfNegative(fileSizeBytes);

        SourcePath = sourcePath;
        Width = width;
        Height = height;
        FramesPerSecond = framesPerSecond;
        FrameCount = frameCount;
        FileSizeBytes = fileSizeBytes;
        Codec = codec ?? string.Empty;
    }

    public string SourcePath { get; }

    public int Width { get; }

    public int Height { get; }

    public double FramesPerSecond { get; }

    // from the container metadata, only an estimate for some formats - decoders must cope w/ an early or late end
    public long FrameCount { get; }

    public long FileSizeBytes { get; }

    /// <summary>FourCC or codec name from the container, e.g. "h264". Info only.</summary>
    public string Codec { get; }

    public double AspectRatio => (double)Width / Height;

    public TimeSpan FrameDuration => TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond / FramesPerSecond));

    public TimeSpan Duration => TimeOfFrame(FrameCount);

    // -1 when the video reports no frames
    public long LastFrameIndex => FrameCount - 1;

    public TimeSpan TimeOfFrame(long frameIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        return TimeSpan.FromTicks((long)Math.Round(frameIndex * (TimeSpan.TicksPerSecond / FramesPerSecond)));
    }

    // frame on screen at this time, clamped
    public long FrameAtTime(TimeSpan time)
    {
        if (FrameCount == 0 || time <= TimeSpan.Zero)
        {
            return 0;
        }

        // the + 1: TimeOfFrame rounds to the nearest tick, so a frame can start half a tick early
        // (29.97, 59.94 fps...). one extra tick keeps FrameAtTime(TimeOfFrame(n)) == n
        var frame = (long)Math.Floor((time.Ticks + 1) * FramesPerSecond / TimeSpan.TicksPerSecond);
        return ClampFrame(frame);
    }

    public long ClampFrame(long frameIndex) => FrameCount == 0 ? 0 : Math.Clamp(frameIndex, 0, LastFrameIndex);

    public bool ContainsFrame(long frameIndex) => frameIndex >= 0 && frameIndex < FrameCount;
}
