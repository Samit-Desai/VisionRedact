# AI Pipeline

## Purpose

Find faces (and, with other models, persons, vehicles, plates) across an entire multi-GB video in the background,
without blocking the UI, with bounded memory, cancellable at any time — and hand the results to a human for review.

## Architecture

```text
AnalysisService.StartAsync(models, sampling)
   └─ DetectorHost.GetAsync(models)          one InferenceSession per model, loaded once, reused
   └─ AnalysisJob (ProcessingJob)            state machine, cancellation, throttled progress
         producer task                                         consumer task
         ─────────────                                         ─────────────
         decoder #2 (sequential)                               detector.Detect(frame, ct)
           ├ not sampled → Skip() (no pixels)                    ├ tracker.Assign (IoU → TrackId)
           └ sampled → ReadNext ─► Channel<OwnedVideoFrame> ─►  └ repository.AddFrameResults
                                   bounded(4), FullMode=Wait      finally: frame.Dispose()
```

Inside one `Detect()` (Infrastructure.AI):

```text
frame (BGR, 1920x1080, native memory)
  → zero-copy Mat header → letterbox resize (INTER_AREA) into reused canvas, pad (114 YOLO / 127 UltraFace)
  → normalize + BGR→RGB + HWC→CHW into reused float[]            (LetterboxPreprocessor)
  → OrtValue wrapping the float[] (pinned, not copied)
  → InferenceSession.Run(RunOptions wired to the CancellationToken)
  → IOutputDecoder over spans of the native outputs              (UltraFaceDecoder / YoloDecoder /
                                                                  YoloEndToEndDecoder, bound by the model's
                                                                  IModelFormatProfile)
  → confidence filter → model-input pixels → LetterboxTransform⁻¹ → NormalizedRect (clamped)
  → min-size filter → NMS (per class, IoU 0.4: NmsDecoder wraps the UltraFace and YOLO decoders;
                          the NMS-free YOLO26 head needs none) → DetectionCandidate[]
```

## Models (multi-model by design)

| Model | Format | Input | Measured Detect() p50 | Notes |
|---|---|---|---|---|
| UltraFace RFB-320 | UltraFace (`scores` [1,N,2], `boxes` [1,N,4]) | 320×240 | 5.5 ms CPU / 2.4 ms GPU | ONNX Model Zoo, MIT. Fast; misses distant faces (0 of ~6 found on the cricket footage at 1080p) |
| YOLO11n face | Ultralytics detect head [1, 4+C, 8400] | 640×640 | 23.4 ms CPU / 4.7 ms GPU | WIDER FACE-trained. Finds ~30 px faces in 1080p. Embedded licence: AGPL-3.0 |
| YOLO26l face | YoloEndToEnd, NMS-free [1, 300, 6] | 1280×1280 | 756 ms CPU / 78 ms GPU (mean of 30, one Tears of Steel frame) | Hugging Face uralman/yolo26l-widerface. Most accurate, slowest. Embedded licence: AGPL-3.0 |
| *Any Ultralytics detect ONNX* | same | from metadata | — | dropped into `models/`, classes mapped from `names` metadata (person, car/bus/truck → Vehicle, license plate) |

`OnnxModelCatalog` builds the list; `OnnxDetectorFactory` validates each model's contract (input `[N,3,H,W]` float;
output shapes for its format) **before** any frame is processed, so a wrong model fails once with a clear message.
Several models can be selected together (`DetectorHost` composes them), e.g. faces + persons.

### Why not tie the app to one YOLO version

The pipeline sees only `IObjectDetector`. Formats differ in output layout, preprocessing (mean/scale/padding) and
class sets; those live in `PreprocessSpec` + an `IOutputDecoder` per family. A new family = one decoder class.

## Coordinates

```text
source 1920×1080 ──letterbox──► model 640×640 (content 640×360 at y=140) ──model box (input px)──►
   LetterboxTransform.ContainerToNormalized ──► NormalizedRect 0..1 (clamped; parts in the bars cut off)
   ──► WPF: LetterboxTransform.Fit(video, surface DIPs).NormalizedToContainer ──► overlay
   ──► export: NormalizedRect.ToPixels (outward rounding) ──► ROI
```

The same `LetterboxTransform` serves model input and WPF display. Domain objects store only normalized boxes.
Edge cases handled and tested: letterbox bars, negative/out-of-range boxes, inverted corners, NaN, zero size.

## Sampling

`IFrameSamplingStrategy`: every frame, every Nth, or a target rate (e.g. 5 per second of a 59.94 fps file = every
~12th frame, decided from the absolute frame index, so it doesn't drift). Between samples, the repository holds a sampled frame's boxes for
`HoldFrames` frames for display and redaction.

| | More inference | Sampling |
|---|---|---|
| Coverage | every frame | short-lived faces can be missed |
| Cost | YOLO: 46 frames/s → a 23-min 59.94 fps file ≈ 30 min | 5/s: ≈ 5 min (measured 29.9 AI frames/s on the real file) |
| Held boxes | exact | a moving face can drift out of a held box → padding (default 20 %) and tracking mitigate |

## Threading

- Producer and consumer are two pool tasks; the decoder and the detector each have exactly one caller.
- `InferenceSession.Run` is thread-safe, but our reused buffers are not → a lock in `OnnxObjectDetector`. Parallelism
  comes from ONNX Runtime's intra-op thread pool.
- Model loading (`DetectorHost`) runs via `Task.Run`, guarded by a `SemaphoreSlim` so concurrent callers load once.

## Memory ownership

| Resource | Owner | Released |
|---|---|---|
| `InferenceSession` (graph, weights, arenas) | `OnnxObjectDetector`, owned by `DetectorHost` | at shutdown, after jobs are cancelled |
| Resize/canvas `Mat`, float tensor | `LetterboxPreprocessor` (per detector) | with the detector |
| input `OrtValue`, `RunOptions`, output `OrtValue`s | one `Detect()` call | `using` before returning |
| queued frames | the channel | consumer `finally`; leftovers drained on stop |

## Cancellation

Cooperative, observed between frames by both tasks. Inside inference, the token sets `RunOptions.Terminate`, which ONNX
Runtime checks between graph nodes — so cancellation latency is bounded by one node, not one full inference.
State: Running → Cancelling → Cancelled; results produced so far are kept.

## Failure behaviour

| Failure | Result |
|---|---|
| model file missing | `DetectionException(ModelNotFound)` + message pointing to `scripts/download-model.ps1` |
| corrupt / wrong model | `InvalidModel` from contract validation |
| onnxruntime.dll missing/incompatible | `RuntimeUnavailable` |
| inference error on a frame | `InferenceFailed` → job **Failed** with the root cause (not the secondary cancellation of the producer); frames released |

## GPU inference (DirectML)

```text
"Run on" picker ─► DetectorHost.Target ─► OnnxDetectorFactory.Create(model, target)
                                            SessionOptions.AppendExecutionProvider_DML(adapterIndex)
                                            (EnableMemoryPattern = false, ORT_SEQUENTIAL)
                                            failure → CPU session, provider "CPU (GPU failed: …)"
DxgiAdapterCatalog: CreateDXGIFactory1 → EnumAdapters1(i) → GetDesc1   (i == DirectML device id)
```

- **Why DirectML, not CUDA**: DirectX 12 works on NVIDIA, AMD and Intel without installing CUDA/cuDNN (several GB) and
  without matching CUDA versions; the reference laptop has both an Intel Arc and an NVIDIA RTX GPU.
- **Why DXGI enumeration**: DirectML's device id *is* the `EnumAdapters1` index; WMI lists GPUs in a different order.
  Implemented with COM vtable function pointers, every interface released in `finally`, software adapters excluded,
  duplicate listings (docks) removed.
- **Sessions per device**: `DetectorHost` caches one session per (model, device); a running job keeps its detector if the
  user switches devices.
- **Measured** (Performance.md): YOLO11n p50 23.4 ms CPU → 14.2 ms Arc → **4.7 ms RTX 3500**; pipeline 39 → 152 frames/s.
- **Version trade-off**: the DirectML ORT build's latest release is 1.24.4 (CPU build: 1.31). Every API used exists in 1.24.
- **Memory**: ~1 GB private bytes with a DirectML session (upload/readback heaps).

## Testing

Unit (fakes): NMS, sampling maths, pipeline ordering, backpressure bound (≤ 6 live frames with a 15 ms detector),
cancellation, root-cause failure, short streams, detector reuse, multi-model composition.
Integration (real ONNX Runtime): model-zoo conformance on CPU and on every GPU (within 3e-7) (our session reproduces the reference outputs within 1e-3),
preprocessing layout (padding, channel order, normalization), blank frames produce nothing, contract validation,
corrupt/missing models, class-name mapping. Optional `VR_FACE_IMAGE` test for a local photo.
