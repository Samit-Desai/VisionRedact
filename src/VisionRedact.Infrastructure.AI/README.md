# VisionRedact.Infrastructure.AI

ONNX Runtime adapter for object detection, on the CPU or a GPU through DirectML.

Dependencies: VisionRedact.Application, `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 (CPU + DirectML execution
providers), `Emgu.CV` (native letterbox resize of frames). x64 only (`RequiresNativeX64`); `AllowUnsafeBlocks` for the
DXGI COM calls.

| Type | Role |
|---|---|
| `OnnxModelCatalog` | `IDetectionModelCatalog`: built-in UltraFace RFB-320 and YOLO11n-face, plus any Ultralytics YOLO `detect` ONNX dropped into `models/`, with classes mapped from its `names` metadata |
| `OnnxDetectorFactory` | `IObjectDetectorFactory.Create(model, target)`: session options, DirectML EP for the chosen adapter with CPU fallback, ORT log level error, model contract validation before any frame is processed; translates failures into `DetectionException` |
| `OnnxObjectDetector` | `IObjectDetector`: owns one `InferenceSession` and its preprocessing buffers; `lock` around reused buffers; `RunOptions.Terminate` wired to cancellation; NMS per class |
| `LetterboxPreprocessor`, `PreprocessSpec` | frame → CHW float tensor (BGR→RGB, letterbox, normalize) into buffers allocated once per detector; zero-copy `Mat` header over the frame |
| `UltraFaceDecoder`, `YoloDecoder` (`IOutputDecoder`) | read model outputs as spans over native memory (no copies), map boxes back through `LetterboxTransform` |
| `DxgiAdapterCatalog` | `IInferenceDeviceCatalog`: DXGI `EnumAdapters1` via COM vtable function pointers (index == DirectML device id); software adapters excluded, duplicates removed; `Preferred()` = most dedicated VRAM; any failure → CPU only |

Rules: no WPF, no Prism, no other Infrastructure project. Sessions are created once per (model, device) and owned by
`DetectorHost` in Application; per-inference `OrtValue`s and `RunOptions` are disposed before `Detect` returns.

Measured (Performance.md): YOLO11n `Detect()` p50 23.4 ms CPU / 14.2 ms Intel Arc / 4.7 ms RTX 3500; 0.3 KB managed
allocation per preprocessed frame. See [../../docs/AiPipeline.md](../../docs/AiPipeline.md).
