using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Export;
using VisionRedact.Application.Media;
using VisionRedact.Application.Redaction;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.Redaction;
using VisionRedact.Infrastructure.Video;

namespace VisionRedact.IntegrationTests;

// a source with audio, made by FFmpeg at test time: 640x360 at 30000/1001 fps, 4 s, and a 1 kHz tone that
// starts 0.5 s after the first frame, so the audio alignment gets tested
public sealed class FfmpegSourceVideo : IDisposable
{
    public FfmpegSourceVideo()
    {
        Directory = Path.Combine(Path.GetTempPath(), "VisionRedact.IntegrationTests", "ffmpeg-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        FilePath = Path.Combine(Directory, "source-with-audio.mp4");
        if (!FfmpegFiles.Installed)
        {
            return;
        }

        var (exit, error) = FfmpegFiles.Run(FfmpegFiles.Ffmpeg,
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30000/1001",
            "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
            "-filter_complex", "[1:a]adelay=500:all=1[a]", "-map", "0:v", "-map", "[a]",
            "-t", "4", "-c:v", "libopenh264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", FilePath);
        if (exit != 0)
        {
            throw new InvalidOperationException("FFmpeg could not create the test source: " + error);
        }
    }

    public string Directory { get; }

    public string FilePath { get; }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class FfmpegFiles
{
    public static string BinDirectory => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public static string Ffmpeg => Path.Combine(BinDirectory, "ffmpeg.exe");

    public static string Ffprobe => Path.Combine(BinDirectory, "ffprobe.exe");

    public static bool Installed => File.Exists(Ffmpeg) && File.Exists(Ffprobe);

    public static (int ExitCode, string Output) Run(string exe, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result); // ffprobe JSON is on stdout; FFmpeg's logs on stderr
    }

    // each stream as ffprobe sees it: type, codec, frame count (video only), duration
    public static IReadOnlyList<(string Type, string Codec, long Frames, double Duration)> Probe(string path)
    {
        var (exit, output) = Run(Ffprobe, "-v", "error", "-count_frames", "-show_entries", "stream=codec_type,codec_name,nb_read_frames,duration", "-of", "json", path);
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("streams").EnumerateArray().Select(s => (
            s.GetProperty("codec_type").GetString()!,
            s.GetProperty("codec_name").GetString()!,
            s.TryGetProperty("nb_read_frames", out var f) && long.TryParse(f.GetString(), out var n) ? n : 0,
            s.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var t) ? t : 0)).ToArray();
    }
}

// skips when FFmpeg isn't downloaded (fresh clone)
public sealed class RequiresFfmpegFactAttribute : FactAttribute
{
    public RequiresFfmpegFactAttribute()
    {
        if (!FfmpegFiles.Installed)
        {
            Skip = "FFmpeg not installed — run scripts/download-ffmpeg.ps1.";
        }
    }
}

// real export through ffmpeg.exe (ADR-0020): GPU or CPU H.264, audio kept, redaction, cancel, fallback
public sealed class FfmpegExportTests : IClassFixture<FfmpegSourceVideo>, IClassFixture<SyntheticVideo>, IDisposable
{
    private readonly FfmpegSourceVideo _source;
    private readonly SyntheticVideo _synthetic;
    private readonly string _output = Directory.CreateTempSubdirectory("vr-ffmpeg-it-").FullName;
    private readonly EmguVideoDecoderFactory _decoders = new();

    public FfmpegExportTests(FfmpegSourceVideo source, SyntheticVideo synthetic)
    {
        _source = source;
        _synthetic = synthetic;
    }

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [RequiresFfmpegFact]
    public async Task Export_writes_h264_with_every_frame_redacted_and_audio_aligned_to_the_video()
    {
        long sourceFrames;
        using (var decoder = _decoders.Open(_source.FilePath))
        {
            sourceFrames = decoder.Metadata.FrameCount;
        }

        var regions = new RedactionRegionStore();
        regions.Add(RedactionRegion.Create(new NormalizedRect(0.25, 0.25, 0.5, 0.5), 0, sourceFrames - 1, RedactionEffectKind.SolidBlack));
        var output = Path.Combine(_output, "ffmpeg.mp4");
        var factory = new FfmpegVideoWriterFactory(new EmguVideoWriterFactory(), ffmpegDirectory: FfmpegFiles.BinDirectory);

        var result = await Service(factory).ExportAsync(new ExportRequest(_source.FilePath, output, ExportFormat.Mp4, RedactionSnapshot.Capture(sourceFrames, new InMemoryDetectionRepository(), regions)));

        Assert.Contains(result.Codec.Split(' ')[0], new[] { "h264_nvenc", "h264_mf", "h264_qsv", "h264_amf", "libopenh264" });
        Assert.EndsWith("+ aac", result.Codec, StringComparison.Ordinal);
        Assert.True(result.AudioPreserved);
        var streams = FfmpegFiles.Probe(output);
        var video = Assert.Single(streams, s => s.Type == "video");
        var audio = Assert.Single(streams, s => s.Type == "audio");
        Assert.Equal("h264", video.Codec);
        Assert.Equal(result.FramesWritten, video.Frames);             // every decoded frame was encoded
        Assert.Equal("aac", audio.Codec);
        Assert.InRange(audio.Duration, video.Duration - 0.1, video.Duration + 0.1); // audio ends with the video (apad + -shortest)

        // the redaction survives encoding: the center of every re-decoded frame is black
        using var reread = _decoders.Open(output);
        var frames = 0;
        while (reread.ReadNext() is { } frame)
        {
            using (frame)
            {
                var centre = frame.GetPixels()[((frame.Height / 2) * frame.Stride) + ((frame.Width / 2) * 3)];
                Assert.InRange(centre, 0, 16);
            }

            frames++;
        }

        Assert.Equal(result.FramesWritten, frames);
    }

    [RequiresFfmpegFact]
    public async Task Audio_that_starts_after_the_first_frame_keeps_its_offset()
    {
        // the source's tone starts 0.5 s in. export, then find where sound begins (silencedetect ends the first silence)
        var output = Path.Combine(_output, "offset.mp4");
        long frames;
        using (var decoder = _decoders.Open(_source.FilePath))
        {
            frames = decoder.Metadata.FrameCount;
        }

        var factory = new FfmpegVideoWriterFactory(new EmguVideoWriterFactory(), ffmpegDirectory: FfmpegFiles.BinDirectory);
        await Service(factory).ExportAsync(new ExportRequest(_source.FilePath, output, ExportFormat.Mp4, RedactionSnapshot.Empty(frames)));

        var (_, log) = FfmpegFiles.Run(FfmpegFiles.Ffmpeg, "-hide_banner", "-nostats", "-i", output, "-af", "silencedetect=noise=-40dB:d=0.1", "-f", "null", "-");
        var silenceEnd = log.Split('\n').Select(l => l.Trim()).First(l => l.Contains("silence_end:", StringComparison.Ordinal));
        var seconds = double.Parse(silenceEnd.Split("silence_end:")[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 0.45, 0.6);
    }

    [RequiresFfmpegFact]
    public async Task Cancelled_export_stops_ffmpeg_and_leaves_no_file()
    {
        var running = FfmpegProcessCount();
        var output = Path.Combine(_output, "cancelled.mp4");
        var slow = new SlowDecoderFactory(_decoders, TimeSpan.FromMilliseconds(20));
        var factory = new FfmpegVideoWriterFactory(new EmguVideoWriterFactory(), ffmpegDirectory: FfmpegFiles.BinDirectory);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VideoExportService(slow, factory, Processor()).ExportAsync(
            new ExportRequest(_source.FilePath, output, ExportFormat.Mp4, RedactionSnapshot.Empty(10_000)), cancellationToken: cts.Token));

        Assert.Empty(Directory.GetFiles(_output));
        Assert.Equal(running, FfmpegProcessCount()); // the encoder process was killed, not orphaned
    }

    [RequiresFfmpegFact]
    public async Task Source_without_audio_is_reported_as_having_none()
    {
        var output = Path.Combine(_output, "silent.mp4");
        var factory = new FfmpegVideoWriterFactory(new EmguVideoWriterFactory(), ffmpegDirectory: FfmpegFiles.BinDirectory);

        var result = await Service(factory).ExportAsync(new ExportRequest(_synthetic.FilePath, output, ExportFormat.Mp4, RedactionSnapshot.Empty(SyntheticVideo.FrameCount)));

        Assert.Equal(ExportAudio.SourceHasNone, result.Audio);
        Assert.DoesNotContain("aac", result.Codec, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_ffmpeg_the_OpenCV_writer_is_used()
    {
        var output = Path.Combine(_output, "fallback.mp4");
        var factory = new FfmpegVideoWriterFactory(new EmguVideoWriterFactory(), ffmpegDirectory: Path.Combine(_output, "no-ffmpeg-here"));

        var result = await Service(factory).ExportAsync(new ExportRequest(_synthetic.FilePath, output, ExportFormat.Mp4, RedactionSnapshot.Empty(SyntheticVideo.FrameCount)));

        Assert.Null(factory.SelectedEncoder);
        Assert.False(result.AudioPreserved);
        Assert.Equal(ExportAudio.NotSupported, result.Audio);
        Assert.Contains(result.Codec, new[] { "avc1", "mp4v" });
        Assert.Equal(SyntheticVideo.FrameCount, result.FramesWritten);
    }

    [RequiresFfmpegFact]
    public async Task Avi_export_keeps_using_Motion_JPEG()
    {
        var factory = new FfmpegVideoWriterFactory(new EmguVideoWriterFactory(), ffmpegDirectory: FfmpegFiles.BinDirectory);

        var result = await Service(factory).ExportAsync(new ExportRequest(_synthetic.FilePath, Path.Combine(_output, "out.avi"), ExportFormat.AviMjpeg, RedactionSnapshot.Empty(SyntheticVideo.FrameCount)));

        Assert.Equal("MJPG", result.Codec);
    }

    [Theory]
    [InlineData(59.94005994005994, "60000/1001")]
    [InlineData(29.97002997002997, "30000/1001")]
    [InlineData(23.976023976023978, "24000/1001")]
    [InlineData(25.0, "25")]
    [InlineData(60.0, "60")]
    [InlineData(12.5, "12.5")]
    public void Frame_rates_are_passed_exactly(double fps, string expected) =>
        Assert.Equal(expected, FfmpegVideoWriterFactory.FrameRate(fps));

    private VideoExportService Service(IVideoWriterFactory writers) => new(_decoders, writers, Processor());

    private static RedactionProcessor Processor() => new([new GaussianBlurEffect(), new PixelateEffect(), new SolidBlackEffect()]);

    private static int FfmpegProcessCount() => Process.GetProcessesByName("ffmpeg").Count(p =>
    {
        try
        {
            return string.Equals(p.MainModule?.FileName, FfmpegFiles.Ffmpeg, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            p.Dispose();
        }
    });

    // stands in for a slow decoder: loops the source, so the export is still running when it gets canceled
    private sealed class SlowDecoderFactory(IVideoDecoderFactory inner, TimeSpan perFrame) : IVideoDecoderFactory
    {
        public IVideoDecoder Open(string path) => new Slow(inner, path, perFrame);

        private sealed class Slow(IVideoDecoderFactory factory, string path, TimeSpan perFrame) : IVideoDecoder
        {
            private IVideoDecoder _inner = factory.Open(path);
            private long _position;

            public Domain.Media.VideoMetadata Metadata => _inner.Metadata;

            public string BackendName => _inner.BackendName;

            public long Position => _position;

            public void Seek(long frameIndex) => throw new NotSupportedException();

            public bool Skip() => throw new NotSupportedException();

            public OwnedVideoFrame? ReadNext()
            {
                Thread.Sleep(perFrame);
                var frame = _inner.ReadNext();
                if (frame is null)
                {
                    _inner.Dispose();
                    _inner = factory.Open(path); // loop: the export must be canceled, not finish
                    frame = _inner.ReadNext();
                }

                _position++;
                return frame;
            }

            public void Dispose() => _inner.Dispose();
        }
    }
}
