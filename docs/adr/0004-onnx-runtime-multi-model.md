# ADR-0004: ONNX Runtime for Inference, Pluggable Models

## Context
Analysis needs an object detector that runs in-process on Windows, on CPU by default and on a GPU when one is present.
The first target is faces, but plates, persons and vehicles are expected, and no single model is best: a small fast
model and a slower, more accurate one are both useful. The pipeline must not change when a model is added.

## Options
1. ML.NET — .NET-native API, but object detection is itself a wrapper over ONNX Runtime with less control over
   sessions, execution providers and buffers.
2. OpenCV DNN (already shipped with Emgu CV) — one fewer dependency, but weaker operator coverage for current YOLO
   exports and no DirectML path.
3. Python sidecar (PyTorch/Ultralytics over IPC) — richest model ecosystem, but a second runtime to deploy and a
   process boundary for 6 MB frames.
4. Cloud vision API — no local compute, but evidence video leaves the machine; latency and cost per frame.
5. ONNX Runtime in-process, models described by data, format-specific pre/post-processing behind small interfaces.

## Decision
Option 5. The Application layer depends only on
[`IObjectDetector`](../../src/VisionRedact.Application/Detections/IObjectDetector.cs) and
`IObjectDetectorFactory.Create(DetectionModel, InferenceTarget)`. A
[`DetectionModel`](../../src/VisionRedact.Application/Detections/DetectionModel.cs) is a pure description (file, format,
class map, default confidence, licence notes). Infrastructure
([`OnnxDetectorFactory`](../../src/VisionRedact.Infrastructure.AI/OnnxDetectorFactory.cs)) validates the model's
input/output contract once and pairs it with a `PreprocessSpec` (input size, mean, scale, pad value) and an
`IOutputDecoder` ([`UltraFaceDecoder`, `YoloDecoder`](../../src/VisionRedact.Infrastructure.AI/OutputDecoders.cs)).
[`OnnxModelCatalog`](../../src/VisionRedact.Infrastructure.AI/OnnxModelCatalog.cs) offers UltraFace RFB-320,
YOLO11n-face, and any Ultralytics `detect` ONNX export dropped into `models/` (classes read from its `names` metadata).
[`DetectorHost`](../../src/VisionRedact.Application/Detections/DetectorHost.cs) keeps one session per (model, device),
created lazily and cached as `model.Id@target.Key`; several models can run as one composite detector.

## Why
An `InferenceSession` holds the optimized graph, weights and memory arenas; creating one costs hundreds of
milliseconds, so it is created once and reused for every frame and job. ONNX is the common export format of the model
ecosystem, so new models arrive as files, not code. Contract validation at load means a wrong model fails once with a
clear `DetectionException` instead of producing silent garbage boxes. The same runtime gives CPU and DirectML
execution providers (ADR-0017).

## Benefits
Adding a person, vehicle or plate detector is "copy the .onnx file". Models are compared on the same footage through
the same pipeline: UltraFace finds 0 of ~6 distant faces in 1080p cricket footage; YOLO11n-face finds ~30 px faces.
A missing or broken model disables only that model.

## Costs
Two decoder families to maintain; only Ultralytics-style YOLO heads and the UltraFace layout are understood. Custom
models need a static output shape. The detector serializes calls with a lock (reused buffers), so parallelism comes
from ONNX Runtime's intra-op threads, not from concurrent `Detect` calls.

## Risks
Model licences. The yolo11n-face weights carry AGPL-3.0 in their embedded licence metadata — acceptable for evaluation,
a concern for commercial distribution. Custom models may map classes wrongly.

## Mitigation
Licence and provenance are part of `DetectionModel.Notes`, shown in the UI and saved with projects; the custom-model
catalog surfaces the embedded `license` metadata. UltraFace (MIT) is always available as a permissive option. Class
names are mapped explicitly (`MapClassName`); unknown classes are ignored rather than redacted as something else.

## When to reconsider
If a model family needs a new output layout (e.g. anchor-free heads with keypoints), add a decoder. If commercial
distribution is planned, replace AGPL weights with a permissively licensed or commercially licensed model.
