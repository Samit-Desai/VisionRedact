# VisionRedact.Infrastructure.Video

Emgu CV adapter (Emgu.CV + Emgu.CV.runtime.windows 4.13.0.5924), plus the optional FFmpeg export writer (an external
`ffmpeg.exe`, no package reference). With Infrastructure.AI, the only project that references `Emgu.CV`.

| Type | Role |
|---|---|
| `EmguVideoDecoderFactory` | opens `VideoCapture` (FFmpeg backend first), validates metadata, translates every failure into `MediaException` |
| `EmguVideoDecoder` | owns the `VideoCapture`; `Read` into a fresh `Mat` per frame (never `QueryFrame`); `Skip()` = grab without retrieve; `Seek` via `CAP_PROP_POS_FRAMES` (frame-exact on the AVCHD test file: 5/5 seeks, pixel diff 0.00); normalizes to BGR24 |
| `MatVideoFrame` | `OwnedVideoFrame` owning one `Mat` |
| `EmguRuntimeProbe` | loads `cvextern.dll` at startup, reports backends or a readable failure |
| `NativeLoadFailure` | classifies `DllNotFound` / `BadImageFormat` / `EntryPointNotFound` / `TypeInitialization` |
| `GaussianBlurEffect`, `PixelateEffect`, `SolidBlackEffect` | `IRedactionEffect` strategies, in place on a zero-copy ROI view; blur of large regions at reduced resolution (61.9 → 1.5 ms per region) |
| `EmguVideoWriterFactory`, `EmguVideoWriter` | `IVideoWriterFactory`/`IVideoWriter`; codec fallback avc1 → mp4v → MJPG (Emgu's constructor throws when openh264 is missing — caught); video only, no audio. Used for AVI, and for MP4 when FFmpeg is absent |
| `FfmpegVideoWriterFactory` | `IVideoWriterFactory` for MP4 through `<app>\ffmpeg\ffmpeg.exe` (`scripts/download-ffmpeg.ps1`); probes H.264 encoders once per process (Media Foundation → NVENC → QSV → AMF → OpenH264; Media Foundation first because it measured faster in the pipeline); aligns the source's first audio track with ffprobe start times, encodes it to AAC 192k (`apad` + `-shortest`); bitrate ≈ 0.1 bit/pixel/frame (2–60 Mbit/s); falls back to the wrapped `EmguVideoWriterFactory` without ffmpeg.exe, for AVI, or when no encoder works |
| `FfmpegVideoWriter` | owns one `ffmpeg.exe` process: raw `bgr24` frames to stdin (span over native memory), `-progress` frame count and error tail from stderr; `Complete()` closes stdin, waits, checks the exit code; `Dispose` without `Complete` kills the process; broken pipe → `ExportException` with ffmpeg's stderr |
| `EmguVisualTrackerFactory`, `EmguVisualTracker` | `IVisualTracker` over OpenCV trackers: KCF (default; 120/120 frames at 21 ms/frame), MIL (120/120, 66 ms); CSRT kept selectable but fails in this Emgu build (0/120) |

Rules: no WPF, no Prism, no other Infrastructure project; every native object has one owner and is disposed
deterministically. x64 only (`RequiresNativeX64`). See [../../docs/ManagedNativeInterop.md](../../docs/ManagedNativeInterop.md).
