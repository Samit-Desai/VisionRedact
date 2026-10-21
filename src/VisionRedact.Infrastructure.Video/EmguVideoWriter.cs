using System.Drawing;
using Emgu.CV;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Export;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Media;

namespace VisionRedact.Infrastructure.Video;

// OpenCV's own writer, video only. FfmpegVideoWriterFactory falls back to it: no ffmpeg.exe, AVI, no working H.264
// encoder, or ffmpeg won't start (ADR-0020).
// MP4: avc1 = H.264 through OpenH264, only if openh264-1.8.0-win64.dll is there (scripts/download-openh264.ps1),
// else mp4v. AVI: MJPG.
// NOTE: asking this writer for hardware encoding never opened, one reason the FFmpeg writer exists
// (Performance.md, optimizations 6 and 7)
public sealed class EmguVideoWriterFactory : IVideoWriterFactory
{
    // codecs that failed in this process, shared by all instances (a set, the byte is unused)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> UnavailableCodecs = new(StringComparer.Ordinal);

    private readonly ILogger _logger;

    public EmguVideoWriterFactory(ILogger<EmguVideoWriterFactory>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>A writer w/ the first codec that opens for the format, or ExportException if none does.</summary>
    public IVideoWriter Create(string path, VideoMetadata source, ExportFormat format)
    {
        var size = new Size(source.Width, source.Height);

        // best first
        string[] codecs = format == ExportFormat.Mp4 ? ["avc1", "mp4v"] : ["MJPG"];

        // failed in this call. they go into UnavailableCodecs only once another codec opens the same path:
        // if every codec fails, the path may be the problem, and nothing is remembered
        var failed = new List<string>();
        foreach (var codec in codecs)
        {
            // a missing encoder dll doesn't appear mid-run, so skip known-missing codecs: Emgu's (caught) throw
            // happens once per process, not on every export
            if (UnavailableCodecs.ContainsKey(codec))
            {
                continue;
            }

            var writer = TryOpen(path, codec, source.FramesPerSecond, size);
            if (writer is { IsOpened: true })
            {
                foreach (var unavailable in failed)
                {
                    UnavailableCodecs.TryAdd(unavailable, 0);
                }

                _logger.LogInformation("Export encoder: {Codec} {Width}x{Height} @ {Fps:F3} fps → {Path}.", codec, size.Width, size.Height, source.FramesPerSecond, path);
                return new EmguVideoWriter(writer, codec, size);
            }

            writer?.Dispose();
            failed.Add(codec);
            TryDelete(path); // a failed open can leave an empty file behind
            _logger.LogInformation("Encoder {Codec} unavailable for {Format}; trying the next one.", codec, format);
        }

        throw new ExportException(ExportErrorKind.EncoderUnavailable, $"No video encoder is available for {format} ({string.Join(", ", codecs)}). Try AVI (Motion-JPEG).");
    }

    // a missing codec makes Emgu throw (NullReferenceException from the failed native ctor, or CvException) instead of
    // returning an unopened writer. both -> null
    private VideoWriter? TryOpen(string path, string codec, double fps, Size size)
    {
        try
        {
            // the FFmpeg backend inside OpenCV, 3-channel BGR frames
            return new VideoWriter(path, (int)VideoCapture.API.Ffmpeg, VideoWriter.Fourcc(codec[0], codec[1], codec[2], codec[3]), fps, size, isColor: true);
        }
        catch (Exception ex) when (ex is NullReferenceException or Emgu.CV.Util.CvException)
        {
            _logger.LogDebug(ex, "OpenCV could not create a {Codec} writer.", codec);
            return null;
        }
    }

    // best effort: if it can't be deleted (locked, no permission), the next try overwrites it anyway
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

// one native VideoWriter: Write per redacted frame, Complete at the end. one export thread, never audio
internal sealed class EmguVideoWriter(VideoWriter writer, string codec, Size size) : IVideoWriter
{
    private long _written;
    private bool _disposed;

    public string Codec { get; } = codec;

    public ExportAudio Audio => ExportAudio.NotSupported;

    public long FramesWritten => _written;

    public void Write(OwnedVideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Width != size.Width || frame.Height != size.Height)
        {
            // VideoWriter silently drops a frame of the wrong size, so fail loudly instead
            throw new ExportException(ExportErrorKind.IncompleteOutput, $"Frame {frame.FrameIndex} is {frame.Width}x{frame.Height}, expected {size.Width}x{size.Height}.");
        }

        using var mat = FrameViews.Wrap(frame); // zero-copy header over the frame's px
        writer.Write(mat);
        _written++;
    }

    // OpenCV finishes the file when the native writer is released, and reports no errors here
    public void Complete() => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // flushes buffered frames, writes the container's index/trailer
        writer.Dispose();
    }
}
