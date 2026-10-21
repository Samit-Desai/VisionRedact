using System.Runtime.InteropServices;
using VisionRedact.Application.Media;

namespace VisionRedact.Application.Tests.Fakes;

// stands in for an Emgu Mat frame, w/ real native memory (NativeMemory): a double free or use-after-dispose
// shows up like it would with a Mat, and leaks can be counted
internal sealed unsafe class TestVideoFrame : OwnedVideoFrame
{
    private const int FrameWidth = 4;
    private const int FrameHeight = 2;
    private byte* _buffer;

    public TestVideoFrame(long frameIndex, TimeSpan timestamp)
        : base(frameIndex, timestamp, FrameWidth, FrameHeight, FrameWidth * 3, FramePixelFormat.Bgr24)
    {
        _buffer = (byte*)NativeMemory.AllocZeroed((nuint)BufferSize);
        _buffer[0] = (byte)(frameIndex % 256); // marker byte, so tests can check the pixels
    }

    protected override IntPtr GetDataPointer() => (IntPtr)_buffer;

    protected override void ReleaseNativeResources()
    {
        NativeMemory.Free(_buffer);
        _buffer = null;
    }
}
