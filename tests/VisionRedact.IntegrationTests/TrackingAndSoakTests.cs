using System.Diagnostics;
using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using VisionRedact.Application.Media;
using VisionRedact.Application.Playback;
using VisionRedact.Domain.Geometry;
using VisionRedact.Infrastructure.Video;

namespace VisionRedact.IntegrationTests;

public sealed class TrackingAndSoakTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vr-track-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(VisualTrackerAlgorithm.Kcf)]
    public void Visual_tracker_follows_a_moving_object_in_real_frames(VisualTrackerAlgorithm algorithm)
    {
        // a textured square moving 4 px a frame to the right over a noisy background.
        // only KCF here: MIL drifts on pure noise like this. how it does on real footage is in ADR-0016
        using var background = new Mat(240, 320, DepthType.Cv8U, 3);
        CvInvoke.Randu(background, new MCvScalar(0, 0, 0), new MCvScalar(60, 60, 60));
        using var texture = new Mat(40, 40, DepthType.Cv8U, 3);
        CvInvoke.Randu(texture, new MCvScalar(150, 150, 150), new MCvScalar(255, 255, 255));

        MatVideoFrame FrameAt(int i)
        {
            var mat = background.Clone();
            using var roi = new Mat(mat, new Rectangle(40 + (i * 4), 100, 40, 40));
            texture.CopyTo(roi);
            return new MatVideoFrame(mat, i, TimeSpan.Zero);
        }

        using var tracker = new EmguVisualTrackerFactory(algorithm).Create();
        using (var first = FrameAt(0))
        {
            tracker.Initialize(first, NormalizedRect.FromPixels(40, 100, 40, 40, 320, 240));
        }

        NormalizedRect box = default;
        for (var i = 1; i <= 30; i++)
        {
            using var frame = FrameAt(i);
            Assert.True(tracker.TryUpdate(frame, out box), $"Lost the target at frame {i}.");
        }

        var expected = NormalizedRect.FromPixels(40 + (30 * 4), 100, 40, 40, 320, 240);
        Assert.True(box.IntersectionOverUnion(expected) > 0.5, $"{algorithm}: tracked {box}, expected about {expected}.");
    }

    [Fact]
    public async Task Long_playback_keeps_native_frames_and_memory_bounded()
    {
        // 5 passes over a 300-frame 720p clip. playing them in real time would be slow, so seek to every 3rd frame
        // instead (each seek decodes + presents one) and sample private bytes after each pass.
        // the measured soak is in Performance.md, "Bounded memory"
        var path = Path.Combine(_directory, "soak.avi");
        using (var writer = new VideoWriter(path, VideoWriter.Fourcc('M', 'J', 'P', 'G'), 30, new Size(1280, 720), true))
        using (var mat = new Mat(720, 1280, DepthType.Cv8U, 3))
        {
            for (var i = 0; i < 300; i++)
            {
                mat.SetTo(new MCvScalar(i % 255, 80, 120));
                writer.Write(mat);
            }
        }

        var liveBefore = OwnedVideoFrame.LiveInstances;
        var sink = new CountingSink();
        var process = Process.GetCurrentProcess();
        var privateSamples = new List<long>();
        for (var pass = 0; pass < 5; pass++)
        {
            await using var session = PlaybackSession.Start(new EmguVideoDecoderFactory().Open(path), sink, TimeProvider.System);
            for (var target = 0; target < 300; target += 3)
            {
                session.Seek(target);
                await sink.WaitForAsync(target);
                Assert.True(OwnedVideoFrame.LiveInstances - liveBefore <= PlaybackOptions.Default.DecodeAheadFrames + 4);
            }

            process.Refresh();
            privateSamples.Add(process.PrivateMemorySize64);
        }

        Assert.Equal(liveBefore, OwnedVideoFrame.LiveInstances);
        var growthMb = (privateSamples[^1] - privateSamples[1]) / 1_048_576.0; // after the first (warm-up) pass
        Assert.True(growthMb < 64, $"Private bytes grew {growthMb:0} MB across passes: {string.Join(", ", privateSamples.Select(s => s / 1_048_576))} MB.");
    }

    // stands in for the WPF presenter: remembers the last frame, disposes it, and lets the test wait for one
    private sealed class CountingSink : IPreviewFrameSink
    {
        private long _last = -1;

        public void Present(OwnedVideoFrame frame)
        {
            Interlocked.Exchange(ref _last, frame.FrameIndex);
            frame.Dispose();
        }

        public void Clear()
        {
        }

        public async Task WaitForAsync(long frame)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Interlocked.Read(ref _last) != frame)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Frame {frame} was not presented.");
                await Task.Delay(1);
            }
        }
    }
}
