using VisionRedact.Domain.Geometry;
using VisionRedact.Infrastructure.Video;

namespace VisionRedact.IntegrationTests;

// OpenCV thumbnails from real decoded frames: aspect ratio kept, region cropped, pixels kept
public sealed class FrameImagingTests : IClassFixture<SyntheticVideo>
{
    private const int JpegTolerance = 3; // the synthetic video is MJPG: gray levels come back within a few units
    private readonly SyntheticVideo _video;
    private readonly EmguFrameImaging _imaging = new();

    public FrameImagingTests(SyntheticVideo video)
    {
        _video = video;
    }

    [Fact]
    public void Whole_frame_is_shrunk_to_fit_keeping_its_aspect_ratio()
    {
        using var decoder = new EmguVideoDecoderFactory().Open(_video.FilePath);
        decoder.Seek(10);
        using var frame = decoder.ReadNext()!;

        var image = _imaging.Resize(frame, null, maxWidth: 80, maxHeight: 80);

        Assert.Equal((80, 60), (image.Width, image.Height)); // 160x120 is 4:3
        Assert.Equal(80 * 3, image.Stride);
        Assert.InRange(image.Pixels[image.Stride * 30 + 40 * 3], (10 * SyntheticVideo.GrayStep) - JpegTolerance, (10 * SyntheticVideo.GrayStep) + JpegTolerance);
    }

    [Fact]
    public void Region_is_cropped_before_resizing_and_never_upscaled()
    {
        using var decoder = new EmguVideoDecoderFactory().Open(_video.FilePath);
        using var frame = decoder.ReadNext()!;

        var image = _imaging.Resize(frame, new NormalizedRect(0.25, 0.25, 0.25, 0.25), maxWidth: 200, maxHeight: 200);

        Assert.Equal((40, 30), (image.Width, image.Height)); // a quarter of each side of 160x120, not upscaled
    }
}
