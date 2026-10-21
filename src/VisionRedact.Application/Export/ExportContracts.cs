using VisionRedact.Application.Media;
using VisionRedact.Domain.Media;

namespace VisionRedact.Application.Export;

// the ports export talks through. the real FFmpeg and OpenCV writers are in Infrastructure.Video,
// the export tests use fakes

public enum ExportFormat
{
    // H.264 + AAC through ffmpeg.exe (ADR-0020), when it's there and an H.264 encoder passes the probe.
    // else the OpenCV writer (avc1, else mp4v), video only
    Mp4 = 0,

    // Motion-JPEG: big files, but OpenCV can always write it. always the OpenCV writer
    AviMjpeg,
}

// why a file has no audio, so the message can say exactly that.
// it used to tell everyone to install FFmpeg, even when the source simply had no audio
public enum ExportAudio
{
    // the OpenCV writer is video only (no FFmpeg, AVI, or no H.264 encoder passed the probe)
    NotSupported,

    SourceHasNone,

    // ffprobe missing or failed: exported w/o audio
    Unreadable,

    // lined up with the first video frame
    Included,
}

// Write every frame, then Complete. Dispose w/o Complete = give up: the FFmpeg writer kills ffmpeg,
// and the caller deletes the temp file either way.
// not thread-safe: the encode task writes, the export thread completes once the stages are done
public interface IVideoWriter : IDisposable
{
    /// <summary>Codec actually used, e.g. "avc1", "mp4v", "MJPG", "h264_mf + aac".</summary>
    string Codec { get; }

    ExportAudio Audio { get; }

    // frames taken so far. after Complete the FFmpeg writer gives ffmpeg's own count (OpenCV just counts writes)
    long FramesWritten { get; }

    // the frame stays the caller's
    void Write(OwnedVideoFrame frame);

    // finishes the file. the FFmpeg writer throws here if ffmpeg failed, not in Dispose
    void Complete();
}

// a port, so the export tests run w/ a fake writer (no files, no codecs)
public interface IVideoWriterFactory
{
    /// <exception cref="ExportException">No encoder could be opened for the format.</exception>
    IVideoWriter Create(string path, VideoMetadata source, ExportFormat format);
}

// the UI only shows the exception's Message. so far just the tests check Kind
public enum ExportErrorKind
{
    Unknown = 0,
    InvalidOutputPath,
    SameAsSource,
    AccessDenied,
    EncoderUnavailable,
    IncompleteOutput,
}

// the one exception for expected export problems (bad path, no encoder, missing frames)
public sealed class ExportException : Exception
{
    public ExportException(ExportErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ExportException()
        : this(ExportErrorKind.Unknown, "Export failed.")
    {
    }

    public ExportException(string message)
        : this(ExportErrorKind.Unknown, message)
    {
    }

    public ExportException(string message, Exception innerException)
        : this(ExportErrorKind.Unknown, message, innerException)
    {
    }

    public ExportErrorKind Kind { get; }
}

public readonly record struct ExportProgress(long FramesWritten, long TotalFrames, long FramesRedacted);

// what came out. ExportMessages turns it into the final message
public sealed record ExportResult(string OutputPath, string Codec, long FramesWritten, long FramesRedacted, TimeSpan Elapsed)
{
    public ExportAudio Audio { get; init; } = ExportAudio.NotSupported;

    // the old bool from before ExportAudio, kept. only the tests read it
    public bool AudioPreserved => Audio == ExportAudio.Included;

    // the container's frame count (only an estimate for some formats), 0 when unknown
    public long FramesExpected { get; init; }

    // decoding stopped well before FramesExpected: a truncated file, or a damaged packet ended it early.
    // the export is a valid video of what could be read, just shorter, so the user must be told
    public bool SourceEndedEarly { get; init; }
}

// Redactions = the snapshot taken when Export was pressed
public sealed record ExportRequest(string SourcePath, string OutputPath, ExportFormat Format, RedactionSnapshot Redactions);

// a port, so the ExportJob tests can use FakeExportService
public interface IVideoExportService
{
    Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default);
}
