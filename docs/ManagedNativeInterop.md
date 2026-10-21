# Managed / Native Interop

The app crosses the managed/native boundary on three in-process paths: video (Emgu CV → OpenCV), inference (ONNX
Runtime → CPU or DirectML) and GPU enumeration (DXGI COM, called directly through vtable function pointers). A fourth
path is out of process: MP4 export hands raw frames to `ffmpeg.exe` through a pipe (optional; see
[FFmpeg as a separate process](#ffmpeg-as-a-separate-process-ffmpegvideowriter)).

## The stacks

```text
C# / WPF                         VisionRedact.Presentation.Wpf
   ↓
Application abstractions         IVideoDecoder, OwnedVideoFrame, IObjectDetector, IInferenceDeviceCatalog  (no native types)
   ↓
┌─ Video ─────────────────────────────────┐ ┌─ Inference ─────────────────────────────────┐ ┌─ GPU list ──────────────┐
│ Infrastructure.Video                    │ │ Infrastructure.AI                           │ │ Infrastructure.AI       │
│ EmguVideoDecoder, MatVideoFrame,        │ │ OnnxDetectorFactory, OnnxObjectDetector,    │ │ DxgiAdapterCatalog      │
│ redaction effects, writer, tracker      │ │ LetterboxPreprocessor (Emgu resize)         │ │   ↓ [DllImport]         │
│   ↓                                     │ │   ↓                                         │ │ dxgi.dll                │
│ Emgu.CV.dll (P/Invoke + IntPtr handles) │ │ Microsoft.ML.OnnxRuntime (managed API)      │ │ CreateDXGIFactory1      │
│   ↓                                     │ │   ↓                                         │ │   ↓ COM vtable calls    │
│ cvextern.dll (Emgu C export layer)      │ │ onnxruntime.dll (DirectML build 1.24.4)     │ │ EnumAdapters1, GetDesc1,│
│   ↓                                     │ │   ├ CPU execution provider                  │ │ Release                 │
│ OpenCV (static in cvextern)             │ │   └ DirectML EP → DirectML.dll → D3D12 GPU  │ └─────────────────────────┘
│   → opencv_videoio_ffmpeg4130_64.dll    │ └─────────────────────────────────────────────┘
└─────────────────────────────────────────┘
```

Emgu is the only OpenCV binding used (not OpenCvSharp). Only `Infrastructure.Video` and `Infrastructure.AI` reference
`Emgu.CV`; only `Infrastructure.AI` references ONNX Runtime. Application code sees pixels only as `IntPtr` + stride or
`ReadOnlySpan<byte>`.

## Deployment

`Emgu.CV.runtime.windows` and `Microsoft.ML.OnnxRuntime.DirectML` place native binaries in the output folder:

```text
bin/…/runtimes/win-x64/native/cvextern.dll                        ~48 MB
bin/…/runtimes/win-x64/native/opencv_videoio_ffmpeg4130_64.dll    ~29 MB
bin/…/runtimes/win-x64/native/onnxruntime.dll                     ~17 MB
bin/…/runtimes/win-x64/native/onnxruntime_providers_shared.dll
bin/…/models/*.onnx                                               copied from models/ (not committed)
bin/…/ffmpeg/ffmpeg.exe, ffprobe.exe, av*.dll, sw*.dll             copied from ffmpeg/bin (optional, not committed)
```

- **x64 only.** Every project that can load native code sets `RequiresNativeX64=true` → `PlatformTarget=x64`
  (`Directory.Build.props`). Domain/Application/Persistence stay AnyCPU. A 32-bit process would load the wrong binary.
- The managed `Emgu.CV.dll` and native `cvextern.dll` versions must match exactly (central package management pins
  both to 4.13.0.5924). The same applies to the ONNX Runtime managed API and `onnxruntime.dll` (1.24.4, one package).
- `cvextern.dll` depends on the Microsoft Visual C++ runtime; a clean machine without it fails with
  `DllNotFoundException` even though the file exists.
- Without its FFmpeg plugin (`opencv_videoio_ffmpeg`), OpenCV falls back to other backends (MSMF) or fails to open common formats.
- FFmpeg (`scripts/download-ffmpeg.ps1`: 9.0.2, BtbN `win64-lgpl-shared` build, SHA-256 pinned) is unpacked into
  `ffmpeg/` (git-ignored); the WPF, benchmark and integration-test projects copy `ffmpeg\bin\*` except `ffplay.exe`
  to `<output>\ffmpeg\`. It is a separate executable, so it has no architecture or version coupling with the app's
  own assemblies; without it, export uses the OpenCV writer.
- **Version trade-off**: the DirectML build of ONNX Runtime is at 1.24.4, its latest release (the CPU-only package is at 1.31).
  DirectML is in sustained engineering; every API the app uses exists in 1.24.

## Resource lifetime across the boundary

A managed Emgu object holds an `IntPtr` to a native object. `Dispose()` calls into `cvextern` to release it; the
native pixel buffer is reference-counted by OpenCV and freed when the last `cv::Mat` referencing it is released.
The finalizer releases leaked objects eventually, but "eventually" is decided by managed allocation pressure, which
native allocations do not create — hence explicit ownership (see [MemoryManagement.md](MemoryManagement.md)).
ONNX Runtime objects (`InferenceSession`, `SessionOptions`, `RunOptions`, `OrtValue`) follow the same rule: each is
disposed by exactly one owner, the per-call ones before `Detect()` returns.

Zero-copy paths:

- `OwnedVideoFrame.DataPointer` is `Mat.DataPointer`. WPF's `WritePixels(rect, IntPtr, size, stride)` reads straight
  from native memory into the bitmap's back buffer.
- `LetterboxPreprocessor` wraps the frame's pointer in a `Mat` *header* (no pixel copy) and resizes from it; redaction
  effects and `EmguVisualTracker` use the same kind of view (`FrameViews.Wrap`).
- The model input is an `OrtValue.CreateTensorValueFromMemory` over a reused `float[]` (pinned, not copied); the output
  decoders read `OrtValue` outputs as spans over native memory, valid until the outputs are disposed.

## ONNX Runtime and DirectML

```text
DetectorHost.Target (the "Run on" picker) ─► OnnxDetectorFactory.Create(model, target)
   SessionOptions: ORT_ENABLE_ALL, ORT_SEQUENTIAL, IntraOpNumThreads, log level ERROR
   target is DirectML?
     ├ EnableMemoryPattern = false
     └ AppendExecutionProvider_DML(target.AdapterIndex)       nodes DirectML cannot run fall back to the CPU EP
   new InferenceSession(path, options)
   validate the model contract (input [N,3,H,W] float; outputs for its format) before any frame is processed
```

- The DirectML device id passed to `AppendExecutionProvider_DML` **is the index returned by
  `IDXGIFactory1::EnumAdapters1`**. WMI and other APIs may list GPUs in a different order, which is why the app
  enumerates adapters through DXGI itself.
- Correctness is checked, not assumed: `Every_gpu_reproduces_the_model_zoo_reference_outputs` runs the model zoo's
  reference input on every hardware GPU and compares with the reference outputs (measured agreement with the CPU
  within 3e-7).
- Cancellation crosses the boundary through `RunOptions.Terminate`, which the native runtime checks between graph nodes.

## DXGI through COM vtables (`DxgiAdapterCatalog`)

A minimal COM client without an interop assembly or extra package:

1. `[DllImport("dxgi.dll")] CreateDXGIFactory1(IID_IDXGIFactory1, out factory)`.
2. `EnumAdapters1` is vtable slot 12 of `IDXGIFactory1` (IUnknown 3 + IDXGIObject 4 + IDXGIFactory 5), called through a
   `delegate* unmanaged[Stdcall]` function pointer, for index 0, 1, 2… until `DXGI_ERROR_NOT_FOUND`.
3. `GetDesc1` is slot 10 of `IDXGIAdapter1`; the result is marshalled into a `DXGI_ADAPTER_DESC1` struct laid out
   exactly as in `dxgi.h` (`fixed char Description[128]`, `nuint` memory sizes).
4. Software adapters (`DXGI_ADAPTER_FLAG_SOFTWARE`, e.g. Microsoft Basic Render Driver) are excluded; duplicate
   listings of the same GPU (docks, multi-monitor) are removed; `Preferred()` picks the GPU with the most dedicated memory.
5. Every COM pointer obtained is released with `IUnknown::Release` (slot 2) in `finally`.

Any failure (`DllNotFoundException`, `EntryPointNotFoundException`, `SEHException`, a failed HRESULT) yields a list
containing only the CPU: GPU enumeration must never prevent the app from starting.

## FFmpeg as a separate process (`FfmpegVideoWriter`)

```text
FfmpegVideoWriterFactory ── first MP4 export ──► probe encoders once (ffmpeg -f lavfi testsrc2, 10 frames, -f null)
   │                                               h264_nvenc → h264_mf → h264_qsv → h264_amf → libopenh264
   │── per export ──► ffprobe source (stream start times) → audio filter (adelay / atrim, apad)
   └──► Process ffmpeg.exe  -f rawvideo -pix_fmt bgr24 -s WxH -framerate 60000/1001 -i pipe:0 [-i source] … out.mp4
          stdin  ◄── FfmpegVideoWriter.Write: span over the frame's native memory (row by row if the stride is padded)
          stderr ──► -progress key=value lines (frame=N) + the last 20 error lines
          Complete(): close stdin → wait for exit → exit code 0? → FramesWritten = ffmpeg's frame count
          Dispose() without Complete(): Kill(entireProcessTree) — abort
```

**Why a process, not P/Invoke into libav\*.** A crash or memory corruption inside FFmpeg or a GPU driver kills
`ffmpeg.exe`, not the app — the mitigation the section on native crashes below recommends. There is no binding layer
to write or keep in step with FFmpeg's C API, and the LGPL binaries are used unmodified. The cost is one copy of each
frame through the pipe (~6 MB at 1080p); our side does not copy (the frame's native buffer is written as a span), and
measured export throughput (147–167 frames/s at 1080p59.94) shows the pipe is not the bottleneck.

**Encoder probe.** Each candidate encodes 10 frames of a test pattern (20 s timeout); the first that exits with 0 is
used for the rest of the process. A missing or too-old driver simply fails its probe — e.g. on the development laptop
NVENC reports "Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0", AMF fails (no AMD
GPU), and Media Foundation (`h264_mf`) is selected — as called, Microsoft's software H.264 encoder on the CPU.

| Failure | Detected by | Result |
|---|---|---|
| `ffmpeg.exe` not installed | `File.Exists` | OpenCV writer (no audio) |
| `ffmpeg.exe` cannot start | `Win32Exception` / `InvalidOperationException` from `Process.Start` | warning logged, OpenCV writer |
| no H.264 encoder passes the probe | every probe exits non-zero | warning logged, OpenCV writer |
| `ffprobe.exe` missing or cannot read the source | `File.Exists` / exit code / unexpected JSON | warning logged, export without audio |
| ffmpeg exits during the export (bad arguments, encoder error, disk full) | `IOException` (broken pipe) on stdin in `Write` | `ExportException(IncompleteOutput)` with the tail of ffmpeg's stderr; temp file deleted |
| ffmpeg fails while finishing the file | non-zero exit code in `Complete()` | `ExportException(IncompleteOutput)` with exit code and stderr tail |
| ffmpeg does not exit within 5 min after stdin is closed | `WaitForExit` timeout in `Complete()` | `ExportException(IncompleteOutput)` |
| cancel / failure elsewhere in the pipeline | `Dispose` without `Complete` | process tree killed; tested: no ffmpeg.exe left running, no output file |

## Failure behaviour

| Failure | Surfaces as | Handled by | User sees |
|---|---|---|---|
| `cvextern.dll` missing / VC++ runtime missing | `DllNotFoundException` (often inside `TypeInitializationException` from `CvInvoke`'s static ctor) | `EmguRuntimeProbe` at startup; factory translates to `MediaErrorKind.NativeRuntimeUnavailable` | red banner; Open disabled; details in log |
| Wrong architecture | `BadImageFormatException` | same | "requires a 64-bit (x64) process" |
| Managed/native version mismatch | `EntryPointNotFoundException` | same | "does not match the Emgu.CV version" |
| Corrupt / unsupported file | `VideoCapture.IsOpened == false` | factory → `UnsupportedFormat` | error dialog, current video kept |
| OpenCV assertion | `CvException` | adapter → `MediaException` | error dialog / faulted playback banner |
| `onnxruntime.dll` missing or incompatible | `DllNotFoundException` / `BadImageFormatException` / `EntryPointNotFoundException` / `TypeInitializationException` | `OnnxDetectorFactory` → `DetectionErrorKind.RuntimeUnavailable` | message; AI disabled, the rest of the app works |
| Model file missing | `File.Exists` check | `ModelNotFound` | message pointing to `scripts/download-model.ps1` |
| Corrupt model / wrong contract | `OnnxRuntimeException` on load, or contract validation | `InvalidModel` | message naming the model and the mismatch |
| DirectML EP cannot be appended (no DirectML, old driver) | `OnnxRuntimeException` / `EntryPointNotFoundException` / `NotSupportedException` | factory logs a warning, uses the CPU EP | execution provider shown as "CPU (DirectML unavailable)" |
| DirectML session creation fails for this model/GPU | `OnnxRuntimeException` | factory retries on the CPU | "CPU (GPU failed: *name*)" in the diagnostics panel |
| Inference error on a frame | `OnnxRuntimeException` in `Run` | `DetectionException(InferenceFailed)` → job **Failed** with that root cause | job error message |
| No D3D / remote desktop / DXGI failure | exception or failed HRESULT | `DxgiAdapterCatalog` | "Run on" lists the CPU only |

`TypeInitializationException` is sticky: once `CvInvoke`'s static constructor fails, the type is unusable for the
life of the process. That is another reason to probe once, deliberately, at startup, off the UI thread.

Native crashes (access violations inside OpenCV, ONNX Runtime or a GPU driver) cannot be caught in .NET. Mitigations:
validate inputs before native calls (ROI clamping in the redaction planner, contract validation of models), never pass
disposed pointers (`ObjectDisposedException` guards on `DataPointer` and `Detect`), release native objects only after
their last user has stopped (shutdown order in [ThreadingModel.md](ThreadingModel.md)), and — for a production system —
consider running decode/inference out-of-process — as export encoding already is (`ffmpeg.exe`).

## Testing

`VisionRedact.IntegrationTests` exercise the real boundaries: OpenCV runtime probe, metadata, sequential decode with
pixel verification, `Skip`, corrupt/missing file translation, disposal, a full playback session with native frame
counting; ONNX Runtime model-zoo conformance on the CPU and on every GPU, preprocessing layout, contract validation,
corrupt/missing models; `Catalog_always_offers_the_cpu_first_and_lists_hardware_gpus` for DXGI enumeration; `FfmpegExportTests` for the
process boundary (ffprobe-verified output, audio alignment, cancel kills the process, fallback without ffmpeg.exe).
Tests that need a model, a GPU or FFmpeg skip with a reason when it is absent. Missing-DLL behaviour is covered by the pure
`NativeLoadFailure` classification plus a manual check (rename `cvextern.dll` in the output folder, start the app →
banner, no crash).
