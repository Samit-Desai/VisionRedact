# VisionRedact.Application

Ports and use cases. References Domain and `Microsoft.Extensions.Logging.Abstractions` only — no Emgu CV, ONNX
Runtime, WPF or Prism.

| Folder | Types | Role |
|---|---|---|
| Media | `IVideoDecoder`, `IVideoDecoderFactory`, `OwnedVideoFrame`, `MediaException`/`MediaErrorKind`, `IMediaRuntimeProbe` | synchronous single-owner frame source (one per pipeline); single-owner frame over native pixels with idempotent dispose and a `LiveInstances` counter |
| Playback | `PlaybackSession`, `PlaybackClock`, `PlaybackOptions`, `IPreviewFrameSink`, `IPreviewFrameProcessor` | decode task + redaction task + present task over `Channel(1)` and `Channel(3, Wait)`; late frames skipped before decoding; lock-free seek mailbox with generations; anchored clock on `TimeProvider`; late-frame drops (max 4 in a row, never the last frame); fault state |
| Workspace | `VideoWorkspace` | opens the new video before closing the old; cancels jobs; resets detections and regions |
| Detections | `IObjectDetector`/`IObjectDetectorFactory`, `DetectionModel`, `IDetectionModelCatalog`, `DetectorOptions`, `InferenceTarget`/`IInferenceDeviceCatalog`, `DetectorHost`, `NonMaxSuppression`, `IDetectionRepository`/`InMemoryDetectionRepository`, `FrameDetectionService` | detector port; one cached session per (model, device); multi-model composite; per-class NMS; indexed repository with hold and track interpolation for display, batched change events, `Version` |
| Analysis | `AnalysisService`, `AnalysisJob`, `IFrameSamplingStrategy` (`EveryFrameSampling`, `EveryNthFrameSampling`, `TargetRateSampling`) | producer (`Skip()` for unsampled frames) → `Channel(4, Wait)` → consumer (detect, track, store); root-cause failure reporting |
| Tracking | `IObjectTracker`/`IouObjectTracker`, `IVisualTracker`/`IVisualTrackerFactory`, `RegionTrackingJob` | IoU on a constant-velocity predicted box + size-gated centre-distance fallback; "track this region" job |
| Redaction | `RedactionPlanner`, `IRedactionEffect`, `IRedactionProcessor`/`RedactionProcessor`, `IRedactionRegionStore`/`RedactionRegionStore`, `RedactionPreviewProcessor` | one planner for preview and export; effect strategies; copy-on-write region store; preview redaction on its own playback task |
| Export | `IVideoExportService`/`VideoExportService`, `ExportJob`, `RedactionSnapshot`, `IVideoWriter`/`IVideoWriterFactory`, `ExportException` | sequential export from a snapshot → temp file → frame-count check → atomic `File.Move` |
| Projects | `ProjectDocument`, `IProjectRepository`, `ProjectService`, `ProjectException` | save/open review sessions, relink moved media with a source fingerprint |
| Jobs | `ProcessingJob`, `IProcessingJob`, `ProcessingJobManager` | state machine, cancellation, throttled progress (250 ms), `CancelAllAsync` |

Rules: no UI-thread assumptions (`ConfigureAwait(false)` throughout); every frame has one owner and is disposed in
`finally`; blocking native work is started with `Task.Run` only at use-case boundaries.

Tests: `tests/VisionRedact.Application.Tests` (117, fakes only). See [../../docs/VideoPipeline.md](../../docs/VideoPipeline.md),
[../../docs/AiPipeline.md](../../docs/AiPipeline.md), [../../docs/ExportPipeline.md](../../docs/ExportPipeline.md) and
[../../docs/ThreadingModel.md](../../docs/ThreadingModel.md).
