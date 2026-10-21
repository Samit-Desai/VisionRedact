# ADR-0021: Model Format Profiles, NMS as a Decorator, NMS-free YOLO (YOLO26)

## Context
Ultralytics YOLO26 (and YOLOv10, or v8/v11 exported with `nms=True`) is *end-to-end*: the network removes duplicate
boxes itself and outputs final rows `[1, K, 6]` = (x1, y1, x2, y2, confidence, class) in model-input pixels.

The detector had two problems with such a model:
- `OnnxDetectorFactory` chose the decoder with a `switch` on `DetectionModelFormat`, and `CreateYolo` accepted any
  `[1, a, b]` output. A `[1, 300, 6]` tensor passed as "300 anchors, 2 classes" and decoded into garbage.
- Non-maximum suppression (NMS) was hard-wired in `OnnxObjectDetector.Detect`. An NMS-free head reports overlapping
  faces on purpose (people hugging); running NMS again would drop one of them.

## Options
1. Add a third `switch` case and an `if (format != YoloEndToEnd)` around NMS in the detector.
2. **A profile per format (strategy) and NMS as a decorator around decoders that need it.**
3. Convert NMS-free exports back to the classic head (`end2end=False`) and support only that.

## Decision
Option 2.
- `IModelFormatProfile { Format; Bind(session, model, w, h) → DecoderBinding(PreprocessSpec, IOutputDecoder) }` with
  `UltraFaceProfile`, `YoloProfile` and `YoloEndToEndProfile`. The factory looks profiles up in a dictionary built in
  its constructor (internal; not registered in DI).
- `NmsDecoder(inner, iou, max)` wraps the UltraFace and classic YOLO decoders. `OnnxObjectDetector` no longer knows
  about NMS.
- `YoloProfile` validates the anchor dimension against the stride grid (`YoloGrid.AnchorCounts`: 8400 or 8500 at
  640×640), so an NMS-free output is rejected with a message that names the right format.
- `YoloEndToEndDecoder` filters by confidence and mapped class (the class id arrives as a float and is rounded), maps
  corners through the letterbox, and caps the count at `MaxDetectionsPerFrame`.
- `OnnxModelCatalog` infers `YoloEndToEnd` for custom Ultralytics models whose metadata has `end2end` or `nms` set to
  `True`.

## Consequences
- A new model family means one profile and one decoder; the factory and the detector stay unchanged.
- 22 decoder tests run on hand-made tensors, no model file needed. They cover both YOLO layouts, NaN scores, the cap,
  the NMS decorator, and NMS-free duplicates being kept.
- **Update 2026-10:** YOLO26 is now the third built-in detector: `yolo26l-face.onnx` (Hugging Face
  uralman/yolo26l-widerface, AGPL-3.0, 99.6 MB, pinned in `scripts/download-model.ps1`). Its ONNX input is dynamic
  and its metadata says it was trained at 1280×1280, so the factory now reads Ultralytics' `imgsz` metadata for
  dynamic inputs instead of assuming 640.
- Measured on one Tears of Steel frame (mean of 30 runs, CPU / RTX 3500 Ada via DirectML): UltraFace 7.7 / 3.9 ms,
  YOLO11n 30.3 / 9.6 ms, YOLO26l at 1280 756 / 78 ms. All three find the close-up face (integration test).
  YOLO26l is the accuracy option for small or crowded faces, used with sampling on a GPU.
