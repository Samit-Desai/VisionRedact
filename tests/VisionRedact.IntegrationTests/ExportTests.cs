using VisionRedact.Application.Detections;
using VisionRedact.Application.Export;
using VisionRedact.Application.Redaction;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.Redaction;
using VisionRedact.Infrastructure.Video;

namespace VisionRedact.IntegrationTests;

// real round trip through OpenCV: decode -> redact -> encode -> decode again
public sealed class ExportTests : IClassFixture<SyntheticVideo>, IDisposable
{
    private readonly SyntheticVideo _video;
    private readonly string _output = Directory.CreateTempSubdirectory("vr-export-it-").FullName;
    private readonly EmguVideoDecoderFactory _decoders = new();

    public ExportTests(SyntheticVideo video)
    {
        _video = video;
    }

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Theory]
    [InlineData(ExportFormat.AviMjpeg, "out.avi")]
    [InlineData(ExportFormat.Mp4, "out.mp4")]
    public async Task Exported_video_has_every_frame_with_the_region_blacked_out_and_the_source_untouched(ExportFormat format, string name)
    {
        var sourceHash = Hash(_video.FilePath);
        var regions = new RedactionRegionStore();
        regions.Add(RedactionRegion.Create(new NormalizedRect(0.25, 0.25, 0.5, 0.5), 10, 19, RedactionEffectKind.SolidBlack));
        var snapshot = RedactionSnapshot.Capture(SyntheticVideo.FrameCount, new InMemoryDetectionRepository(), regions);
        var service = new VideoExportService(_decoders, new EmguVideoWriterFactory(), new RedactionProcessor([new GaussianBlurEffect(), new PixelateEffect(), new SolidBlackEffect()]));
        var output = Path.Combine(_output, name);

        var result = await service.ExportAsync(new ExportRequest(_video.FilePath, output, format, snapshot));

        Assert.Equal(SyntheticVideo.FrameCount, result.FramesWritten);
        Assert.Equal(10, result.FramesRedacted);
        Assert.Equal(sourceHash, Hash(_video.FilePath)); // source never modified

        using var reread = _decoders.Open(output);
        Assert.Equal(SyntheticVideo.Width, reread.Metadata.Width);
        var frames = 0;
        while (reread.ReadNext() is { } frame)
        {
            using (frame)
            {
                var centre = CentreByte(frame);
                if (frame.FrameIndex is >= 10 and <= 19)
                {
                    Assert.InRange(centre, 0, 12);                               // redacted: black (lossy codecs: near 0)
                }
                else if (frame.FrameIndex >= 5)
                {
                    Assert.InRange(centre, SyntheticVideo.ExpectedGrayLevel(frame.FrameIndex) - 12, 255); // untouched gray level
                }
            }

            frames++;
        }

        Assert.Equal(SyntheticVideo.FrameCount, frames);
    }

    private static int CentreByte(Application.Media.OwnedVideoFrame frame) =>
        frame.GetPixels()[((frame.Height / 2) * frame.Stride) + ((frame.Width / 2) * 3)];

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
