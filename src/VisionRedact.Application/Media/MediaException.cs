namespace VisionRedact.Application.Media;

// what kind of media failure, so callers don't parse message text. the shell logs it and picks the error detail by it
public enum MediaErrorKind
{
    Unknown = 0,
    FileNotFound,
    AccessDenied,

    // corrupt file, unsupported codec, not a video
    UnsupportedFormat,

    // it opened, but the size, frame rate or frame count is unusable
    InvalidMetadata,

    // cvextern.dll / OpenCV didn't load: file missing, wrong architecture, no VC++ runtime
    NativeRuntimeUnavailable,

    DecodeFailed,
}

// Infrastructure turns DllNotFound, TypeInitialization, CvException, IOException... into this, so the UI shows a clear
// message w/o knowing Emgu CV. the original stays in InnerException for the log
public sealed class MediaException : Exception
{
    public MediaException(MediaErrorKind kind, string message, string? sourcePath = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        SourcePath = sourcePath;
    }

    // the 3 standard exception ctors (CA1032)
    public MediaException()
        : this(MediaErrorKind.Unknown, "A media error occurred.")
    {
    }

    public MediaException(string message)
        : this(MediaErrorKind.Unknown, message)
    {
    }

    public MediaException(string message, Exception innerException)
        : this(MediaErrorKind.Unknown, message, null, innerException)
    {
    }

    public MediaErrorKind Kind { get; }

    public string? SourcePath { get; }
}
