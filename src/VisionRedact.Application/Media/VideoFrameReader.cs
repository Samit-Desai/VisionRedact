using System.Diagnostics;

namespace VisionRedact.Application.Media;

// "give me frame N" on its own decoder, for the hover preview and the People tab thumbnails.
// own decoder b/c a decoder has one read position: reading through playback's would make the video jump.
// lazy: it opens on the first read, so a video costs nothing extra until someone hovers.
// one read at a time (SemaphoreSlim(1, 1) as an async lock), on a pool thread. VideoWorkspace owns it and
// disposes it w/ the video
public sealed class VideoFrameReader : IAsyncDisposable
{
    private readonly IVideoDecoderFactory _factory;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IVideoDecoder? _decoder; // guarded by _gate
    private bool _disposed; // guarded by _gate

    // written under _gate, read w/o a lock by the Diagnostics panel
    private bool _decoderOpen;
    private int _framesHeld;
    private int _peakFramesHeld;
    private long _reads;
    private long _readTicks;

    public VideoFrameReader(IVideoDecoderFactory factory, string path)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _factory = factory;
        _path = path;
    }

    // from the first read until dispose
    public bool IsDecoderOpen => Volatile.Read(ref _decoderOpen);

    // 1 while a frame is being projected, else 0
    public int FramesHeld => Volatile.Read(ref _framesHeld);

    // reads are one at a time, so at most 1
    public int PeakFramesHeld => Volatile.Read(ref _peakFramesHeld);

    // seek + decode + projection of the reads that got a frame. not the wait for the lock, not the first open
    public TimeSpan AverageReadTime
    {
        get
        {
            var reads = Interlocked.Read(ref _reads);
            return reads == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Interlocked.Read(ref _readTicks) / reads);
        }
    }

    /// <summary>Frame N (clamped) through project; null at a broken end of the stream or once closed.</summary>
    // the frame is only valid inside project, it's disposed right after. throws MediaException if the decoder fails.
    // a canceled request still waiting for the lock decodes nothing
    public async Task<T?> ReadAsync<T>(long frameIndex, Func<OwnedVideoFrame, T> project, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(project);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _disposed ? null : await Task.Run(() => Read(frameIndex, project), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // wait for a running read, so the decoder is never disposed under it
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _decoder?.Dispose();
            _decoder = null;
            Volatile.Write(ref _decoderOpen, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // under _gate, on a pool thread
    private T? Read<T>(long frameIndex, Func<OwnedVideoFrame, T> project)
        where T : class
    {
        if (_decoder is null)
        {
            _decoder = _factory.Open(_path);
            Volatile.Write(ref _decoderOpen, true);
        }

        var started = Stopwatch.GetTimestamp(); // after the one-time open
        var target = _decoder.Metadata.ClampFrame(frameIndex);
        if (_decoder.Position != target)
        {
            _decoder.Seek(target); // skipped when the very next frame is wanted
        }

        var frame = _decoder.ReadNext();
        if (frame is null)
        {
            return null;
        }

        // one read at a time, so a plain compare + write keeps the peak exact
        var held = Interlocked.Increment(ref _framesHeld);
        if (held > Volatile.Read(ref _peakFramesHeld))
        {
            Volatile.Write(ref _peakFramesHeld, held);
        }

        try
        {
            return project(frame);
        }
        finally
        {
            // even if project throws
            frame.Dispose();
            Interlocked.Decrement(ref _framesHeld);
            Interlocked.Add(ref _readTicks, Stopwatch.GetElapsedTime(started).Ticks);
            Interlocked.Increment(ref _reads);
        }
    }
}
