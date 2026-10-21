using System.Text;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Media;

namespace VisionRedact.Infrastructure.Video;

// opens a video and turns every low-level failure into a MediaException w/ a kind (not found, access denied,
// unsupported, broken OpenCV install, bad metadata), b/c native code only ever says "could not open".
// each consumer gets its own decoder w/ its own position: VideoWorkspace, AnalysisJob, VideoExportService...
public sealed class EmguVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly ILogger _logger;

    public EmguVideoDecoderFactory(ILogger<EmguVideoDecoderFactory>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Opens the file for decoding. Throws MediaException with a clear reason on failure.</summary>
    public IVideoDecoder Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // managed checks first, they give precise messages
        var fileSize = CheckFileAccess(path);

        // non-null until the decoder owns it, so finally frees it on any failure
        VideoCapture? capture = null;
        try
        {
            // FFmpeg on purpose: the same behavior on every PC, not whatever CAP_ANY picks first (e.g. MSMF).
            // seek accuracy depends on the backend (ADR-0018)
            capture = new VideoCapture(path, VideoCapture.API.Ffmpeg);
            if (!capture.IsOpened)
            {
                // FFmpeg can't open it: let OpenCV try any other backend it has
                capture.Dispose();
                capture = new VideoCapture(path, VideoCapture.API.Any);
            }

            if (!capture.IsOpened)
            {
                throw new MediaException(
                    MediaErrorKind.UnsupportedFormat,
                    $"'{Path.GetFileName(path)}' could not be opened as a video. The file may be corrupt or use an unsupported codec.",
                    path);
            }

            var metadata = ReadMetadata(capture, path, fileSize);
            var decoder = new EmguVideoDecoder(capture, metadata);
            capture = null; // the decoder owns it now
            return decoder;
        }
        catch (MediaException)
        {
            // already classified, the filters below must not wrap it again
            throw;
        }
        catch (Exception ex) when (NativeLoadFailure.IsNativeLoadFailure(ex))
        {
            _logger.LogError(ex, "Native OpenCV runtime failed to load while opening {Path}.", path);
            throw new MediaException(MediaErrorKind.NativeRuntimeUnavailable, NativeLoadFailure.Describe(ex), path, ex);
        }
        catch (CvException ex)
        {
            _logger.LogError(ex, "OpenCV failed to open {Path}.", path);
            throw new MediaException(MediaErrorKind.UnsupportedFormat, $"OpenCV could not read '{Path.GetFileName(path)}': {ex.ErrorMessage}", path, ex);
        }
        finally
        {
            capture?.Dispose();
        }
    }

    private static long CheckFileAccess(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new MediaException(MediaErrorKind.FileNotFound, $"The file '{path}' does not exist.", path);
            }

            // open + close a read handle, so a permission problem shows up as one.
            // ReadWrite share: another player may have it open, or it's still being copied
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
            {
            }

            return info.Length;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MediaException(MediaErrorKind.AccessDenied, $"Access to '{path}' was denied.", path, ex);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new MediaException(MediaErrorKind.AccessDenied, $"'{path}' could not be read: {ex.Message}", path, ex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new MediaException(MediaErrorKind.FileNotFound, $"The file '{path}' does not exist.", path, ex);
        }
    }

    // size, fps, frame count, codec. refuses values the timeline, seeking and export can't work with
    private static VideoMetadata ReadMetadata(VideoCapture capture, string path, long fileSize)
    {
        // OpenCV hands every property back as a double
        var width = (int)capture.Get(CapProp.FrameWidth);
        var height = (int)capture.Get(CapProp.FrameHeight);
        var fps = capture.Get(CapProp.Fps);
        var frameCount = (long)capture.Get(CapProp.FrameCount);
        var codec = DecodeFourCc(capture.Get(CapProp.FourCC));

        if (width <= 0 || height <= 0)
        {
            throw new MediaException(MediaErrorKind.InvalidMetadata, "The video reports no frame size.", path);
        }

        if (!double.IsFinite(fps) || fps <= 0 || fps > 1000)
        {
            throw new MediaException(MediaErrorKind.InvalidMetadata, $"The video reports an unusable frame rate ({fps}).", path);
        }

        if (frameCount <= 0)
        {
            // TODO: no frame count (live / fragmented streams) would need a full scan. out of MVP scope, ok for now
            throw new MediaException(MediaErrorKind.InvalidMetadata, "The video does not report a frame count.", path);
        }

        return new VideoMetadata(path, width, height, fps, frameCount, fileSize, codec);
    }

    // FourCC number -> its 4 letters, e.g. 0x31637661 -> "avc1"
    private static string DecodeFourCc(double value)
    {
        var code = (int)value;
        if (code <= 0)
        {
            return "unknown";
        }

        // first letter in the lowest byte. some files put control chars in unused slots, skip those
        var builder = new StringBuilder(4);
        for (var i = 0; i < 4; i++)
        {
            var c = (char)((code >> (8 * i)) & 0xFF);
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? "unknown" : builder.ToString().Trim();
    }
}
