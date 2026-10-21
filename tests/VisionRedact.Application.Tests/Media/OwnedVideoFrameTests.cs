using VisionRedact.Application.Media;
using VisionRedact.Application.Tests.Fakes;

namespace VisionRedact.Application.Tests.Media;

public class OwnedVideoFrameTests
{
    [Fact]
    public void Dispose_is_idempotent()
    {
        var frame = new TestVideoFrame(1, TimeSpan.Zero);

        frame.Dispose();
        frame.Dispose(); // a second dispose must not double-free native memory

        Assert.True(frame.IsDisposed);
    }

    [Fact]
    public void Pixel_access_after_dispose_throws_instead_of_reading_freed_memory()
    {
        var frame = new TestVideoFrame(1, TimeSpan.Zero);
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.DataPointer);
        Assert.Throws<ObjectDisposedException>(() => frame.GetPixels().Length);
    }

    [Fact]
    public void Pixels_expose_the_native_buffer()
    {
        using var frame = new TestVideoFrame(7, TimeSpan.Zero);

        var pixels = frame.GetPixels();

        Assert.Equal(frame.Stride * frame.Height, pixels.Length);
        Assert.Equal(7, pixels[0]);
    }

    [Fact]
    public void Concurrent_dispose_releases_exactly_once()
    {
        for (var i = 0; i < 200; i++)
        {
            var frame = new CountingFrame();
            Parallel.For(0, 8, _ => frame.Dispose());
            Assert.Equal(1, frame.ReleaseCount);
        }
    }

    [Fact]
    public void Stride_smaller_than_a_row_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CountingFrame(stride: 5));
    }

    private sealed class CountingFrame(int stride = 12) : OwnedVideoFrame(0, TimeSpan.Zero, 4, 1, stride, FramePixelFormat.Bgr24)
    {
        private int _releaseCount;

        public int ReleaseCount => Volatile.Read(ref _releaseCount);

        protected override IntPtr GetDataPointer() => IntPtr.Zero;

        protected override void ReleaseNativeResources() => Interlocked.Increment(ref _releaseCount);
    }
}
