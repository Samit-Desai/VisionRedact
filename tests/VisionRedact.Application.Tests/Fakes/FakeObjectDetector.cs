using VisionRedact.Application.Detections;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Application.Tests.Fakes;

// stands in for the ONNX detector: one face per frame, its x taken from the frame index (never random).
// can be slow (Latency) or fail at a frame, and records every call
internal sealed class FakeObjectDetector : IObjectDetector
{
    private int _calls;
    private int _inCall;
    private int _concurrentCalls;

    public DetectorInfo Info { get; } = new("Fake", "fake.onnx", "Face", 320, 240, "CPU");

    public TimeSpan Latency { get; init; }

    // frames where a face is "found", default all of them
    public Func<long, bool> HasFace { get; init; } = _ => true;

    public long? FailAtFrame { get; init; }

    public int Calls => Volatile.Read(ref _calls);

    public int ConcurrentCalls => Volatile.Read(ref _concurrentCalls);

    public bool IsDisposed { get; private set; }

    public List<long> SeenFrames { get; } = [];

    public IReadOnlyList<DetectionCandidate> Detect(OwnedVideoFrame frame, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _inCall, 1) != 0)
        {
            Interlocked.Increment(ref _concurrentCalls);
        }

        try
        {
            ObjectDisposedException.ThrowIf(frame.IsDisposed, frame); // the caller must keep the frame alive
            Interlocked.Increment(ref _calls);
            lock (SeenFrames)
            {
                SeenFrames.Add(frame.FrameIndex);
            }

            if (Latency > TimeSpan.Zero)
            {
                // blocks on purpose, like the real native inference. cancel works like RunOptions.Terminate
                cancellationToken.WaitHandle.WaitOne(Latency);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (FailAtFrame == frame.FrameIndex)
            {
                throw new DetectionException(DetectionErrorKind.InferenceFailed, $"Simulated inference failure at {frame.FrameIndex}.");
            }

            if (!HasFace(frame.FrameIndex))
            {
                return [];
            }

            var x = (frame.FrameIndex % 80) / 100.0;
            return [new DetectionCandidate(new NormalizedRect(x, 0.2, 0.1, 0.15), 0.9f, ObjectClass.Face)];
        }
        finally
        {
            Volatile.Write(ref _inCall, 0);
        }
    }

    public void Dispose() => IsDisposed = true;
}

// stands in for OnnxDetectorFactory: counts the creates and records the device each one asked for
internal sealed class FakeObjectDetectorFactory(Func<IObjectDetector> create) : IObjectDetectorFactory
{
    private int _creates;

    public static DetectionModel Model { get; } = CreateModel("fake");

    public int Creates => Volatile.Read(ref _creates);

    public static DetectionModel CreateModel(string id) =>
        new(id, id, id + ".onnx", DetectionModelFormat.Yolo, new Dictionary<int, ObjectClass> { [0] = ObjectClass.Face }, 0.5f, "test", IsAvailable: true);

    public List<InferenceTarget> Targets { get; } = [];

    public IObjectDetector Create(DetectionModel model, InferenceTarget target)
    {
        Targets.Add(target);
        Interlocked.Increment(ref _creates);
        return create();
    }
}
