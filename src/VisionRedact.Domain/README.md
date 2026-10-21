# VisionRedact.Domain

Pure model with **no package references**.

| Area | Types |
|---|---|
| Media | `VideoMetadata` — validated video facts and frame/time arithmetic under the constant-frame-rate assumption (`TimeOfFrame`, `FrameAtTime`, `ClampFrame`) |
| Geometry | `NormalizedRect` (0..1, clamped on construction; outward-rounding `ToPixels`; IoU; `Lerp`), `LetterboxTransform` (model input ↔ source and video ↔ surface mapping), `PixelRect`, `SizeD`, `PointD`, `RectD` |
| Detections | `Detection` (frame, box, confidence, `ObjectClass`, `ReviewStatus` Pending/Approved/Rejected, `TrackId`), `DetectionTrack` (per-track summary) |
| Redaction | `RedactionRegion` (manual box over a frame range, optional tracked `Path` of `TrackPoint`s, `BoxAt(frame)`), `RedactionSettings`, `RedactionEffectKind` |
| Jobs | `ProcessingJobState`, `JobStateMachine` (Created → Running → Cancelling → Cancelled / Completed / Failed) |

Rule: nothing here may know about pixels in memory, threads, files, OpenCV, ONNX Runtime or WPF. Boxes are stored
normalized; conversion to pixels happens at the edges.

Tests: `tests/VisionRedact.Domain.Tests` (62).
