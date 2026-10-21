using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using VisionRedact.Application.Diagnostics;
using VisionRedact.Application.Media;
using VisionRedact.Application.Playback;
using VisionRedact.Presentation.Wpf.Infrastructure;

namespace VisionRedact.Presentation.Wpf.Rendering;

/// <summary>The playback sink: preview frames onto one reused WriteableBitmap, w/ very little UI-thread work.</summary>
// present task -> Present (one-slot mailbox) -> RenderPending on the UI thread -> WritePixels -> FrameRendered
// only render changes, never layout: the same bitmap every frame, only its px change, so no rebind and no
// measure / arrange. a new bitmap per frame was considered and turned down: big allocations, GC pauses and a
// rebind every frame (ADR-0011).
// a newer frame replaces one the UI never drew, so the screen is never more than one frame behind.
// at most one RenderPending queued, so a fast engine can't flood the Dispatcher and starve input.
// FrameRendered fires in the same Dispatcher job as WritePixels, so the boxes and the picture show up together
public sealed class WriteableBitmapFramePresenter : IPreviewFrameSink
{
    private readonly IUiThread _ui;
    private readonly ILogger _logger;
    private readonly Action _renderCallback;
    // the one-slot mailbox: the present task puts, the UI thread takes. Interlocked only
    private OwnedVideoFrame? _pending;
    // 1 while a RenderPending is queued, else 0. an int b/c Interlocked needs one
    private int _renderQueued;
    // for Diagnostics. written on two threads, so Interlocked
    private long _framesReceived;
    private long _framesRendered;
    private long _framesDropped;
    // UI thread only. its px are overwritten in place
    private WriteableBitmap? _bitmap;
    // WritePixels time on the UI thread, for Diagnostics
    private readonly RecentDurations _presentTimes = new();

    public WriteableBitmapFramePresenter(IUiThread ui, ILogger<WriteableBitmapFramePresenter> logger)
    {
        _ui = ui;
        _logger = logger;
        _renderCallback = RenderPending; // cached delegate, no allocation per Post
    }

    /// <summary>Raised on the UI thread when a new bitmap is made (first frame or a size change).</summary>
    public event EventHandler? SurfaceChanged;

    /// <summary>Raised on the UI thread right after a frame's pixels went into the bitmap.</summary>
    public event EventHandler<FrameRenderedEventArgs>? FrameRendered;

    /// <summary>The bitmap to bind to an Image. UI thread only.</summary>
    public ImageSource? Surface => _bitmap;

    // written on the present task and the UI thread, read by Diagnostics: Interlocked both ways
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    /// <summary>Preview frames replaced before the UI thread could show them.</summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Recent WritePixels times on the UI thread. WPF's GPU upload later, on its render thread, isn't in it.</summary>
    public DurationSummary PresentTimes => _presentTimes.Summarize();

    /// <summary>Present task only. Takes ownership: the frame is disposed after drawing, or when a newer one replaces it.</summary>
    public void Present(OwnedVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Interlocked.Increment(ref _framesReceived);

        // swap the new frame in and take the old one out, in one step (the UI thread may be emptying the slot)
        var stale = Interlocked.Exchange(ref _pending, frame);
        if (stale is not null)
        {
            // never drawn and never will be, a newer one is here: free its native memory now
            stale.Dispose();
            Interlocked.Increment(ref _framesDropped);
        }

        // queue a RenderPending only if none is queued yet (0 -> 1)
        if (Interlocked.CompareExchange(ref _renderQueued, 1, 0) == 0)
        {
            // Post = BeginInvoke, the present task doesn't wait. Render priority runs ahead of input,
            // in step w/ WPF's own render pass
            _ui.Post(_renderCallback, DispatcherPriority.Render);
        }
    }

    // on close, after the session has stopped (VideoWorkspace.CloseCurrentAsync): free a frame still waiting
    public void Clear() => Interlocked.Exchange(ref _pending, null)?.Dispose();

    // UI thread, queued by Present. copies the newest frame into the bitmap and says which frame that is
    private void RenderPending()
    {
        // trick: reset the flag BEFORE taking the frame. a frame that lands after this line queues a new
        // RenderPending, so none can get stuck in the slot
        Volatile.Write(ref _renderQueued, 0);
        var frame = Interlocked.Exchange(ref _pending, null);
        if (frame is null)
        {
            // Clear() ran, or an earlier RenderPending already took it
            return;
        }

        long frameIndex;
        try
        {
            // a new bitmap only on the first frame or a size change
            EnsureSurface(frame.Width, frame.Height);
            // one copy from the native buffer into the back buffer. WritePixels marks it dirty for the next render
            var copying = Stopwatch.GetTimestamp();
            _bitmap!.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.DataPointer, frame.BufferSize, frame.Stride);
            _presentTimes.Add(Stopwatch.GetElapsedTime(copying));
            frameIndex = frame.FrameIndex;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render preview frame {Frame}.", frame.FrameIndex);
            return;
        }
        finally
        {
            // copied (or failed): the native frame isn't needed anymore
            frame.Dispose();
        }

        Interlocked.Increment(ref _framesRendered);
        // same Dispatcher job as WritePixels, so boxes moved by the listeners show in the same frame as these px
        FrameRendered?.Invoke(this, new FrameRenderedEventArgs(frameIndex));
    }

    // UI thread. same size = keep the bitmap, so bindings and layout stay quiet. else a new one + SurfaceChanged,
    // so the view re-reads Surface
    private void EnsureSurface(int width, int height)
    {
        if (_bitmap is not null && _bitmap.PixelWidth == width && _bitmap.PixelHeight == height)
        {
            return;
        }

        // 96 DPI: one video px = one DIP before the Image's Stretch=Uniform.
        // Bgr24 = OpenCV's layout, so WritePixels is a plain copy. NOTE: WPF may still convert it when it composes;
        // Bgr32 / D3DImage is a listed candidate in docs/Performance.md
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }
}

// the frame index now in the bitmap
public sealed class FrameRenderedEventArgs(long frameIndex) : EventArgs
{
    public long FrameIndex { get; } = frameIndex;
}
