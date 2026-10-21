using System.Collections.Concurrent;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Media;

namespace VisionRedact.Application.Tests.Fakes;

// stands in for the OpenCV decoder: frame N carries marker N % 256. can be slow, fail from a frame on,
// and counts overlapping calls (the real decoder is not thread-safe)
internal sealed class FakeVideoDecoder : IVideoDecoder
{
    private readonly ConcurrentQueue<TestVideoFrame> _created = new();
    private readonly long _decodableFrames;
    private long _position;
    private int _inCall;
    private int _concurrentCallViolations;
    private int _seekCount;
    private volatile bool _disposed;

    public FakeVideoDecoder(long frameCount = 100, double fps = 100, TimeSpan? decodeLatency = null, long? decodableFrames = null, string path = "C:\\videos\\fake.mp4")
    {
        Metadata = new VideoMetadata(path, 4, 2, fps, frameCount, 1024, "fake");
        DecodeLatency = decodeLatency ?? TimeSpan.Zero;
        _decodableFrames = decodableFrames ?? frameCount;
    }

    public VideoMetadata Metadata { get; }

    public string BackendName => "FAKE";

    public long Position => Interlocked.Read(ref _position);

    public TimeSpan DecodeLatency { get; set; }

    // ReadNext throws at this frame and every later one
    public long? FailAtFrame { get; set; }

    public bool IsDisposed => _disposed;

    public int SeekCount => Volatile.Read(ref _seekCount);

    public int ConcurrentCallViolations => Volatile.Read(ref _concurrentCallViolations);

    public IReadOnlyCollection<TestVideoFrame> CreatedFrames => _created.ToArray();

    public void Seek(long frameIndex)
    {
        using var _ = EnterCall();
        Interlocked.Increment(ref _seekCount);
        Interlocked.Exchange(ref _position, Metadata.ClampFrame(frameIndex));
    }

    public OwnedVideoFrame? ReadNext()
    {
        using var _ = EnterCall();
        var index = Position;
        if (index >= _decodableFrames)
        {
            return null;
        }

        if (DecodeLatency > TimeSpan.Zero)
        {
            // blocks on purpose, like a native decode
            Thread.Sleep(DecodeLatency);
        }

        if (FailAtFrame is { } failAt && index >= failAt)
        {
            throw new InvalidOperationException($"Simulated decoder failure at frame {index}.");
        }

        var frame = new TestVideoFrame(index, Metadata.TimeOfFrame(index));
        _created.Enqueue(frame);
        Interlocked.Increment(ref _position);
        return frame;
    }

    public bool Skip()
    {
        using var _ = EnterCall();
        if (Position >= _decodableFrames)
        {
            return false;
        }

        Interlocked.Increment(ref _position);
        return true;
    }

    public void Dispose() => _disposed = true;

    private CallScope EnterCall()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _inCall, 1) != 0)
        {
            Interlocked.Increment(ref _concurrentCallViolations);
        }

        return new CallScope(this);
    }

    private readonly struct CallScope(FakeVideoDecoder owner) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref owner._inCall, 0);
    }
}

// hands out whatever decoder the test gives it
internal sealed class FakeVideoDecoderFactory(Func<string, IVideoDecoder> open) : IVideoDecoderFactory
{
    public IVideoDecoder Open(string path) => open(path);
}
