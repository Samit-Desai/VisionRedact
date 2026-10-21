using Emgu.CV;

namespace VisionRedact.Infrastructure.Video;

// OpenCV's thread pool, shared by every OpenCV call. set once, before any other one (App.OnStartup, the benchmarks).
// the last step to 4K60 preview wasn't in the player: capping this pool at 4 was. this worked great
// (Performance.md, optimization 8). likely why: OpenCV split each tiny per-face call over one thread per CPU,
// and the busy pool took cores the decoder and the UI thread needed. 4 still leaves some speed for whole-frame work.
// Emgu's build ignores OPENCV_FOR_THREADS_NUM, so it's set in code.
// remember: a whole-frame effect (e.g. full-frame blur) would be slower w/ 4, measure it then
public static class OpenCvThreads
{
    // or the CPU count, if smaller
    public const int Default = 4;

    // env var override, for measurements. -1 = OpenCV's own default
    public const string OverrideVariable = "VISIONREDACT_OPENCV_THREADS";

    /// <summary>Sets the pool size (the override if set, else Default) and returns the size in effect.</summary>
    public static int Configure()
    {
        CvInvoke.NumThreads = int.TryParse(Environment.GetEnvironmentVariable(OverrideVariable), out var threads)
            ? threads
            : Math.Min(Default, Environment.ProcessorCount);
        return CvInvoke.NumThreads;
    }
}
