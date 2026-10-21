using Emgu.CV;
using VisionRedact.Application.Media;

namespace VisionRedact.Infrastructure.Video;

// an OwnedVideoFrame over an Emgu Mat that only this frame holds. EmguVideoDecoder makes it; after that it has one
// owner at a time, and the owner disposes it (ADR-0008).
// Dispose -> Mat.Dispose -> cvextern frees the cv::Mat (its px go when the native refcount hits 0).
// never lean on Emgu's finalizer: the GC can't see native memory, it would let frames pile up
internal sealed class MatVideoFrame : OwnedVideoFrame
{
    private readonly Mat _mat;

    // mat: one nobody else references (never a buffer the capture reuses), this frame owns it now. Step = the stride
    public MatVideoFrame(Mat mat, long frameIndex, TimeSpan timestamp)
        : base(frameIndex, timestamp, mat.Width, mat.Height, checked((int)mat.Step), FramePixelFormat.Bgr24)
    {
        _mat = mat;
    }

    // address of the first px, the base class hands out spans over it
    protected override IntPtr GetDataPointer() => _mat.DataPointer;

    // the base Dispose calls this exactly once
    protected override void ReleaseNativeResources() => _mat.Dispose();
}
