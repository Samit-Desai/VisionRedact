namespace VisionRedact.Application.Media;

// checks at startup that the native OpenCV DLLs load. a missing cvextern.dll then shows as a clear status bar
// message (Open stays disabled), not a TypeInitializationException on the first Open.
// ShellViewModel.InitializeAsync calls it once, EmguRuntimeProbe (Infrastructure) does the work
public interface IMediaRuntimeProbe
{
    /// <summary>Blocking, call it off the UI thread. Never throws.</summary>
    MediaRuntimeStatus Probe();
}

// VideoBackends e.g. FFMPEG, MSMF. ErrorDetail = the technical part when it's unavailable (status bar tooltip)
public sealed record MediaRuntimeStatus(bool IsAvailable, string Summary, IReadOnlyList<string> VideoBackends, string? ErrorDetail = null)
{
    public static MediaRuntimeStatus Unavailable(string summary, string errorDetail) => new(false, summary, [], errorDetail);
}
