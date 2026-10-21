using Emgu.CV;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Media;

namespace VisionRedact.Infrastructure.Video;

// startup health check: ShellViewModel runs Probe on the pool and shows the answer in the status bar. never throws.
// careful: not the first OpenCV call anymore. OpenCvThreads.Configure() in App.OnStartup sets CvInvoke.NumThreads
// first, on the UI thread, so a missing cvextern.dll throws there, before Probe can report it
public sealed class EmguRuntimeProbe : IMediaRuntimeProbe
{
    private readonly ILogger _logger;

    public EmguRuntimeProbe(ILogger<EmguRuntimeProbe>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Is native OpenCV usable: its version and video backends, or why not.</summary>
    public MediaRuntimeStatus Probe()
    {
        // only the x64 cvextern.dll ships, a 32-bit process can never load it
        if (!Environment.Is64BitProcess)
        {
            return MediaRuntimeStatus.Unavailable(
                "OpenCV unavailable: 32-bit process",
                "VisionRedact ships the x64 build of cvextern.dll; run the application as a 64-bit process.");
        }

        try
        {
            if (!CvInvoke.Init())
            {
                return MediaRuntimeStatus.Unavailable("OpenCV unavailable", "CvInvoke.Init() reported that native modules are not loaded.");
            }

            // the video I/O backends OpenCV has, e.g. FFMPEG, MSMF (Media Foundation)
            var backends = CvInvoke.Backends.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var version = typeof(CvInvoke).Assembly.GetName().Version?.ToString() ?? "unknown";
            var summary = $"OpenCV ready (Emgu CV {version}; video backends: {string.Join(", ", backends)})";
            _logger.LogInformation("{Summary}", summary);
            return new MediaRuntimeStatus(true, summary, backends);
        }
        catch (Exception ex) when (NativeLoadFailure.IsNativeLoadFailure(ex))
        {
            _logger.LogError(ex, "Native OpenCV runtime failed to load.");
            return MediaRuntimeStatus.Unavailable("OpenCV unavailable", NativeLoadFailure.Describe(ex) + Environment.NewLine + ex.Message);
        }
        catch (Exception ex)
        {
            // catch-all on purpose: a health check reports a problem, it never crashes the app
            _logger.LogError(ex, "Unexpected error while probing the OpenCV runtime.");
            return MediaRuntimeStatus.Unavailable("OpenCV unavailable", ex.Message);
        }
    }
}
