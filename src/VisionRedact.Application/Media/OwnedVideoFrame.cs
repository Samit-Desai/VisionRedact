namespace VisionRedact.Application.Media;

// one decoded picture, pixels usually in native memory, with exactly one owner at a time (ADR-0008).
// the decoder creates it, the caller of ReadNext owns it. an owner hands it on (and never touches it again)
// or disposes it in a finally. never rely on the GC: it only sees a tiny managed object, not the MBs behind it.
// implementations must never wrap a buffer the decoder reuses (e.g. the Mat from OpenCV's QueryFrame),
// the next decode would overwrite frames still waiting in a queue.
// may move between threads, never used by two at once
public abstract class OwnedVideoFrame : IDisposable
{
    // process-wide leak counter. frames come and go on many threads, hence Interlocked
    private static long s_liveInstances;

    private static long s_peakLiveInstances;

    // 1 = disposed. Interlocked.Exchange, so only one caller ever frees the memory
    private int _disposed;

    protected OwnedVideoFrame(long frameIndex, TimeSpan timestamp, int width, int height, int stride, FramePixelFormat pixelFormat)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * BytesPerPixel(pixelFormat));

        FrameIndex = frameIndex;
        Timestamp = timestamp;
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = pixelFormat;

        // counted only after the checks passed, so a frame that failed to build is never counted
        var live = Interlocked.Increment(ref s_liveInstances);

        // CAS loop, another thread may raise the peak at the same time
        long peak;
        while (live > (peak = Interlocked.Read(ref s_peakLiveInstances))
               && Interlocked.CompareExchange(ref s_peakLiveInstances, live, peak) != peak)
        {
        }
    }

    // frames alive right now, whole process. Diagnostics shows it, tests check it goes back down.
    // must stay bounded by the queue sizes, never grow w/ the video length
    public static long LiveInstances => Interlocked.Read(ref s_liveInstances);

    // exact max of LiveInstances since the last reset, so a short spike between two polls isn't missed (benchmarks)
    public static long PeakLiveInstances => Interlocked.Read(ref s_peakLiveInstances);

    public static void ResetPeakLiveInstances() => Interlocked.Exchange(ref s_peakLiveInstances, LiveInstances);

    public long FrameIndex { get; }

    public TimeSpan Timestamp { get; }

    public int Width { get; }

    public int Height { get; }

    // bytes per row, padding included
    public int Stride { get; }

    public FramePixelFormat PixelFormat { get; }

    public int BufferSize => checked(Stride * Height);

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>First pixel. Only valid until Dispose, never cache it.</summary>
    // the presenter's WritePixels and the zero-copy Mat wrappers read through it
    public IntPtr DataPointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return GetDataPointer();
        }
    }

    // a span, not a Memory<byte>, so nobody can store it past the frame's life (use after free)
    public unsafe ReadOnlySpan<byte> GetPixels() => new((void*)DataPointer, BufferSize);

    public static int BytesPerPixel(FramePixelFormat format) => format switch
    {
        FramePixelFormat.Bgr24 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    // safe to call many times, even from 2 threads at once. only the first call frees
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            ReleaseNativeResources();
        }
        finally
        {
            // un-count even if the release threw, else LiveInstances shows a false leak
            Interlocked.Decrement(ref s_liveInstances);
        }

        GC.SuppressFinalize(this);
    }

    // e.g. the Mat's data. only called while not disposed
    protected abstract IntPtr GetDataPointer();

    // called exactly once, must not throw
    protected abstract void ReleaseNativeResources();

    public override string ToString() => $"Frame #{FrameIndex} {Width}x{Height} @ {Timestamp}";
}
