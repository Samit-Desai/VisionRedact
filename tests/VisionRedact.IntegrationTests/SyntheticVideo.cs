using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace VisionRedact.IntegrationTests;

// makes a tiny video at test time w/ the real OpenCV VideoWriter, so no media file is committed.
// frame N is solid gray N * GrayStep. MJPEG keeps that level within a few units, so a test can tell it got
// the right frame after a decode or a seek
public sealed class SyntheticVideo : IDisposable
{
    public const int Width = 160;
    public const int Height = 120;
    public const double Fps = 30;
    public const int FrameCount = 60;
    public const int GrayStep = 4;

    public SyntheticVideo()
    {
        Directory = Path.Combine(Path.GetTempPath(), "VisionRedact.IntegrationTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        FilePath = Path.Combine(Directory, "synthetic.avi");

        // MJPG in AVI uses OpenCV's own writer, no FFmpeg encoder needed
        using (var writer = new VideoWriter(FilePath, VideoWriter.Fourcc('M', 'J', 'P', 'G'), Fps, new Size(Width, Height), isColor: true))
        {
            if (!writer.IsOpened)
            {
                throw new InvalidOperationException("OpenCV VideoWriter could not create the synthetic test video.");
            }

            using var frame = new Mat(Height, Width, DepthType.Cv8U, 3);
            for (var i = 0; i < FrameCount; i++)
            {
                var level = ExpectedGrayLevel(i);
                frame.SetTo(new MCvScalar(level, level, level));
                writer.Write(frame);
            }
        }

        CorruptFilePath = Path.Combine(Directory, "corrupt.mp4");
        var garbage = new byte[64 * 1024];
        new Random(42).NextBytes(garbage);
        File.WriteAllBytes(CorruptFilePath, garbage);
    }

    public string Directory { get; }

    public string FilePath { get; }

    public string CorruptFilePath { get; }

    public static int ExpectedGrayLevel(long frameIndex) => (int)(frameIndex * GrayStep);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort, a handle may still be closing on a slow machine
        }
    }
}
