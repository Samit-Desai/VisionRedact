# Architecture

Status: **implemented** — every layer, project and pipeline described here exists in `src/` and is covered by the
346 tests (Domain 76, Application 182, Integration 88).

## Purpose

Keep volatile technology (Emgu CV, ONNX Runtime, WPF, Prism) at the edges so the parts that encode the product's
rules — frame ownership, timing, job state, redaction decisions — are testable without a UI or native code, and
replaceable one adapter at a time.

## Solution structure

```text
src/
  VisionRedact.Domain                    entities & value objects: VideoMetadata, NormalizedRect, LetterboxTransform,
                                         Detection (ReviewStatus, TrackId), DetectionTrack, RedactionRegion (+ tracked Path),
                                         RedactionSettings, ProcessingJobState + JobStateMachine
  VisionRedact.Application               ports (interfaces), pipelines, use cases: PlaybackSession, VideoWorkspace,
                                         AnalysisJob/AnalysisService, DetectorHost, InMemoryDetectionRepository,
                                         IouObjectTracker, RegionTrackingJob, RedactionPlanner, VideoExportService/ExportJob,
                                         ProjectService, ProcessingJob/ProcessingJobManager
  VisionRedact.Infrastructure.Video      Emgu CV: EmguVideoDecoder(+Factory), MatVideoFrame, EmguRuntimeProbe,
                                         GaussianBlurEffect/PixelateEffect/SolidBlackEffect, EmguVideoWriter(+Factory),
                                         EmguVisualTracker(+Factory); FfmpegVideoWriter(+Factory): MP4 export through an
                                         external ffmpeg.exe (optional), falling back to EmguVideoWriterFactory
  VisionRedact.Infrastructure.AI         ONNX Runtime (CPU + DirectML): OnnxDetectorFactory, OnnxObjectDetector,
                                         LetterboxPreprocessor, UltraFaceDecoder/YoloDecoder, OnnxModelCatalog,
                                         DxgiAdapterCatalog
  VisionRedact.Infrastructure.Persistence  JsonProjectRepository (.vrproject, versioned JSON DTOs, atomic save)
  VisionRedact.Presentation.Wpf          Prism shell, feature views/view models, custom controls (VideoTimelineControl,
                                         VideoSurface), WriteableBitmapFramePresenter, Dispatcher boundary (IUiThread)
tests/
  VisionRedact.Domain.Tests              pure functions
  VisionRedact.Application.Tests         fakes: FakeVideoDecoder, FakeObjectDetector, FakeExportService,
                                         FakeVideoWriterFactory, RecordingFrameSink, TestVideoFrame
  VisionRedact.IntegrationTests          real OpenCV, FFmpeg and ONNX inference (CPU and every GPU) on synthetic videos
tools/
  VisionRedact.Benchmarks                B1–B8 measurement harness (Performance.md)
```

## Layer dependencies

```text
          Presentation.Wpf  (composition root)
          │        │
          │        └──────────────┐
          ▼                       ▼
     Application ◄──── Infrastructure.Video / .AI / .Persistence
          │
          ▼
        Domain
```

Rules (enforced by project references; Domain has **no** package references at all):

| Project | May reference | Must not reference |
|---|---|---|
| Domain | nothing | everything else |
| Application | Domain, Microsoft.Extensions.Logging.Abstractions | Emgu CV, OpenCV, ONNX Runtime, WPF, Prism, Dispatcher, WriteableBitmap |
| Infrastructure.Video | Application, Emgu CV (+ native runtime package) | WPF, Prism, other Infrastructure projects |
| Infrastructure.AI | Application, ONNX Runtime (DirectML build), Emgu CV (native letterbox resize of frames) | WPF, Prism, other Infrastructure projects |
| Infrastructure.Persistence | Application (System.Text.Json is part of the BCL) | WPF, Prism, Emgu CV, ONNX Runtime, other Infrastructure projects |
| Presentation.Wpf | everything (it composes) | business rules — it only adapts |

Infrastructure depends on Application (not the reverse): Application defines the ports, infrastructure implements
them. This is dependency inversion in the literal sense — the policy owns the interface.

## Main interfaces — each exists for a reason

| Interface | Layer | Why it exists | Implemented by |
|---|---|---|---|
| `IVideoDecoder` | Application | Playback, analysis, export, tracking and "detect this frame" each need an independent sequential/seekable frame source; Application cannot see `VideoCapture` | `EmguVideoDecoder` |
| `IVideoDecoderFactory` | Application | Each pipeline opens its **own** decoder over the same file; tests substitute fakes | `EmguVideoDecoderFactory` |
| `OwnedVideoFrame` (abstract class) | Application | Explicit single-owner frame whose pixels live in native memory; zero-copy access via pointer/span | `MatVideoFrame` |
| `IPreviewFrameSink` | Application | The only path for frames to reach the UI; lets the WPF presenter drop stale frames | `WriteableBitmapFramePresenter` |
| `IPreviewFrameProcessor` | Application | Hook on the playback redaction task that modifies decoded preview frames (preview redactions) | `RedactionPreviewProcessor` |
| `IMediaRuntimeProbe` | Application | Turns a missing `cvextern.dll` into a status message at startup | `EmguRuntimeProbe` |
| `IObjectDetector`, `IObjectDetectorFactory` | Application | Faces, persons, vehicles, plates without touching the pipeline; `Create(model, target)` picks CPU or a GPU | `OnnxObjectDetector`, `OnnxDetectorFactory` |
| `IDetectionModelCatalog` | Application | Models are data (files + metadata), not code | `OnnxModelCatalog` |
| `IInferenceDeviceCatalog` | Application | "Run on" choice: CPU or a DirectML GPU by adapter index | `DxgiAdapterCatalog` |
| `IFrameSamplingStrategy` | Application | Every frame / every N / target rate are policies, not pipeline code | `EveryFrameSampling`, `EveryNthFrameSampling`, `TargetRateSampling` |
| `IProcessingJob`, `IProcessingJobManager` | Application | Uniform lifecycle, progress and cancellation for analysis, export and region tracking | `AnalysisJob`, `ExportJob`, `RegionTrackingJob` (all `ProcessingJob`); `ProcessingJobManager` |
| `IDetectionRepository` | Application | 50k+ detections need indexed lookup by frame, hold/interpolation for display and review status; storage is replaceable | `InMemoryDetectionRepository` |
| `IObjectTracker` | Application | Detection → track identity during analysis | `IouObjectTracker` |
| `IVisualTracker`, `IVisualTrackerFactory` | Application | Pixel-based tracking of a manual region | `EmguVisualTracker`, `EmguVisualTrackerFactory` (KCF by default) |
| `IRedactionEffect`, `IRedactionProcessor` | Application | Strategy: blur / pixelate / solid box, dispatched by `RedactionEffectKind` | `GaussianBlurEffect`, `PixelateEffect`, `SolidBlackEffect`; `RedactionProcessor` |
| `IRedactionRegionStore` | Application | Manual regions + settings, read without locks by the playback redaction task and export | `RedactionRegionStore` (copy-on-write) |
| `IVideoWriter`, `IVideoWriterFactory` | Application | Encoding is replaceable; `Complete()` finishes the file, `Dispose` alone aborts | `FfmpegVideoWriterFactory` (ffmpeg.exe, H.264 + audio; registered in `App.xaml.cs`) wrapping `EmguVideoWriterFactory` as fallback |
| `IVideoExportService` | Application | Export is a separate pipeline; tests use `FakeExportService` | `VideoExportService` |
| `IProjectRepository` | Application | `.vrproject` persistence separate from media | `JsonProjectRepository` |

Considered and rejected:

- **`IVideoMetadataReader`** — metadata is a property of an open decoder; a second interface would open the file
  twice and duplicate error translation. Revisit if metadata must be read for many files without decoding (e.g. a
  media library view).
- **`IPlaybackClock`** — .NET's `TimeProvider` already abstracts a monotonic clock and has a test double
  (`FakeTimeProvider`). A custom interface would add nothing.
- **An interface for `PlaybackSession` / `VideoWorkspace` / `DetectorHost` / `AnalysisService`** — concrete classes
  with a single implementation; they are tested directly with fakes of their *dependencies*. An interface can be
  extracted if a ViewModel test needs it.

## Prism / WPF composition

```text
App : PrismApplication (composition root, App.xaml.cs)
 ├─ OnStartup          → Serilog logger factory, global exception handlers
 ├─ RegisterTypes      → DryIoc: ports → adapters, singletons, dialogs, navigation views
 ├─ ConfigureViewModelLocator → explicit View ↔ ViewModel map (feature folders, no naming magic)
 ├─ CreateShell        → ShellWindow(ShellViewModel)
 └─ OnInitialized      → RegisterViewWithRegion("Player", PlayerView)
                          "Inspector" region: RequestNavigate between
                            MetadataView · DetectionsView · RedactionView · DiagnosticsView
```

| Prism feature | Used for | Not used for |
|---|---|---|
| `BindableBase` | all ViewModels | — |
| `DelegateCommand` | synchronous commands (transport, cancel export, review actions) | async work (see `AsyncCommand`) |
| `IRegionManager` | shell layout: `Player` region; `Inspector` region navigating between four views | — |
| `INavigationAware` | `DetectionsViewModel`, `RedactionViewModel`, `DiagnosticsViewModel` (diagnostics samples only while visible) | — |
| `IEventAggregator` | coarse events only: `VideoOpenedEvent`, `SelectedDetectionChangedEvent`, `SeekRequestedEvent`, `JobCompletedEvent` (ADR-0010) | **per-frame or per-detection data** — frames use `IPreviewFrameSink`; analysis uses Channels; the detection list drains repository changes at 4 Hz |
| `IDialogService` | error dialog with technical details | — |

Option records (`PlaybackOptions`, `DetectorOptions`, `AnalysisOptions`) are registered with `RegisterInstance`:
DryIoc would otherwise try to satisfy the record's compiler-generated copy constructor, recursively.

Why `AsyncCommand` next to `DelegateCommand`: Prism 8's `DelegateCommand` is synchronous. Wrapping an async lambda in
it silently produces `async void` with no re-entrancy guard and no error routing. `AsyncCommand` is the one sanctioned
async-void boundary (`ICommand.Execute`), disables itself while running and routes exceptions to a handler.
