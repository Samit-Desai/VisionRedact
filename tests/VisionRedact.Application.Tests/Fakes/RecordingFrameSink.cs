using VisionRedact.Application.Media;
using VisionRedact.Application.Playback;

namespace VisionRedact.Application.Tests.Fakes;

// stands in for the WPF presenter: records each frame, checks it was still alive, then disposes it like the
// real one must. tests wait on a condition instead of sleeping
internal sealed class RecordingFrameSink : IPreviewFrameSink
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly List<long> _presented = [];
    private readonly List<byte> _markers = [];
    private readonly List<(Func<IReadOnlyList<long>, bool> Condition, TaskCompletionSource Signal)> _waiters = [];
    private int _clearCalls;

    public IReadOnlyList<long> Presented
    {
        get
        {
            lock (_gate)
            {
                return _presented.ToArray();
            }
        }
    }

    public IReadOnlyList<byte> Markers
    {
        get
        {
            lock (_gate)
            {
                return _markers.ToArray();
            }
        }
    }

    public int ClearCalls => Volatile.Read(ref _clearCalls);

    public int ReceivedDisposedFrames { get; private set; }

    public void Present(OwnedVideoFrame frame)
    {
        try
        {
            lock (_gate)
            {
                if (frame.IsDisposed)
                {
                    ReceivedDisposedFrames++;
                    return;
                }

                _presented.Add(frame.FrameIndex);
                _markers.Add(frame.GetPixels()[0]);
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Condition(_presented))
                    {
                        _waiters[i].Signal.TrySetResult();
                        _waiters.RemoveAt(i);
                    }
                }
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    public void Clear() => Interlocked.Increment(ref _clearCalls);

    public Task WaitUntilAsync(Func<IReadOnlyList<long>, bool> condition, TimeSpan? timeout = null)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (condition(_presented))
            {
                return Task.CompletedTask;
            }

            _waiters.Add((condition, signal));
        }

        return signal.Task.WaitAsync(timeout ?? DefaultTimeout);
    }

    public Task WaitForFrameAsync(long frameIndex, TimeSpan? timeout = null) =>
        WaitUntilAsync(p => p.Count > 0 && p[^1] == frameIndex, timeout);
}
