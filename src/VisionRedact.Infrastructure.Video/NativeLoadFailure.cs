namespace VisionRedact.Infrastructure.Video;

// how a missing or wrong cvextern.dll shows up in .NET:
// DllNotFound = the dll or a dependency (e.g. the VC++ runtime) isn't there. BadImageFormat = wrong architecture
// (x86 process, x64 dll). EntryPointNotFound = Emgu.CV.dll and cvextern.dll versions differ. TypeInitialization = any
// of these from CvInvoke's static ctor, and after that CvInvoke is dead for the whole process.
// EmguRuntimeProbe and EmguVideoDecoderFactory turn them into one clear sentence
internal static class NativeLoadFailure
{
    // this exception, or one it wraps, means "native OpenCV didn't load"
    public static bool IsNativeLoadFailure(Exception exception)
    {
        // the real cause is often wrapped, e.g. in a TypeInitializationException
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return true;
            }
        }

        // a static ctor failure w/o a known inner cause still counts, but only for Emgu types
        return exception is TypeInitializationException { TypeName: not null } tie && tie.TypeName.StartsWith("Emgu.", StringComparison.Ordinal);
    }

    // one sentence for the user. the first known exception in the chain wins
    public static string Describe(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case BadImageFormatException:
                    return "The native OpenCV library (cvextern.dll) does not match the process architecture. VisionRedact requires a 64-bit (x64) process.";
                case EntryPointNotFoundException:
                    return "The native OpenCV library (cvextern.dll) does not match the Emgu.CV managed assembly version.";
                case DllNotFoundException:
                    return "The native OpenCV library (cvextern.dll) or one of its dependencies (e.g. the Microsoft Visual C++ runtime) could not be found.";
            }
        }

        return "The native OpenCV runtime could not be initialized.";
    }
}
