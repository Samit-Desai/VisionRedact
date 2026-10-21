# Testing Strategy

## Goals

Test engineering behaviour that is easy to break and hard to see: timing, ordering, ownership, concurrency,
backpressure, error translation, coordinate mapping, export correctness and model conformance. Do not test that WPF
can bind a property.

## Layers

```text
Domain.Tests          pure functions                      ~0.1 s   no I/O
Application.Tests     pipelines with fakes                ~3 s     no WPF, no Emgu, no ONNX Runtime, no native DLLs
IntegrationTests      real OpenCV, FFmpeg, ONNX Runtime   ~22 s    synthetic videos generated at test time; CPU and every GPU
Benchmarks (B1–B8)    measurements, not assertions        —        tools/VisionRedact.Benchmarks → Performance.md
Manual demo           WPF app                             —        see README
```

**346 tests, all passing, none skipped** with models, FFmpeg and GPUs present: Domain 76, Application 182, Integration 88
(counts include theory cases; durations from a Debug run on the reference laptop).

## What each project covers

### VisionRedact.Domain.Tests (62)

| Test class | Covers |
|---|---|
| `VideoMetadataTests` | frame↔time round-trip at 23.976/29.97/59.94 fps (caught a real rounding bug: `FrameAtTime` needed a +1 tick bias), duration, clamping, validation, empty video |
| `NormalizedRectTests` | clamping of out-of-range/negative/inverted/NaN boxes, outward rounding in `ToPixels` (redaction never misses a pixel), round trips, `Inflate`, `Offset`, IoU, `Lerp` |
| `PixelRectTests` | `ClampTo` partially/fully outside the frame |
| `LetterboxTransformTests` | bars top/bottom and left/right, model box → normalized source box, boxes in the bars clipped, empty sizes |
| `JobStateMachineTests` | allowed/forbidden transitions, terminal vs active states |

### VisionRedact.Application.Tests (117)

| Test class | Covers |
|---|---|
| `PlaybackSessionTests` | order, pause, seek, clamping, **seek coalescing**, steps, end/restart, short streams, **decoder fault → Faulted**, **late-frame dropping under slow decode**, **commands don't block while the decoder is busy**, **no concurrent decoder calls under a 400-op storm**, **every frame disposed exactly once** |
| `DecodeAheadTests` | buffer bound while paused, bounded live frames with a fast producer, seek flushes and disposes buffered frames, step-forward from the buffer, `Refresh`, preview processor runs on every frame and its failures do not stop playback |
| `PlaybackClockTests` | anchor-based due times, "late" only after a whole slot, stopped clock, re-anchor — with `FakeTimeProvider` |
| `OwnedVideoFrameTests` | idempotent and racing dispose, `LiveInstances` |
| `VideoWorkspaceTests` | open, second open releases the first decoder, failed open keeps the current video, cancellation during open, concurrent open rejected, dispose |
| `ProcessingJobTests` | every state transition, exception → Failed without escaping, cancel before start / while running, foreign-token OCE is a failure, `CancelAllAsync`, progress throttling |
| `AnalysisJobTests` | every sampled frame analyzed once in order, **`Skip()` for unsampled frames**, **bounded channel holds back a fast producer**, cancellation releases every frame, **root-cause failure**, decoder failure, short streams, detector handle released |
| `FrameSamplingTests` | every frame / every Nth / target rate, hold frames covering the gap, invalid parameters |
| `DetectorHostTests`, `FrameDetectionServiceTests` | load once and reuse, load failure then retry, multi-model composition, one session per (model, device), dispose; single-frame detection stored in the repository |
| `NonMaxSuppressionTests` | overlap collapse, score order, per-class suppression, max results, exclusive IoU threshold |
| `InMemoryDetectionRepositoryTests` | frame lookup, **hold** and **track interpolation** for display, re-analysis keeps human review, immutable status updates, track-level review, ordering, analyzed-frame bit set, 50,000 detections, **concurrent writers and readers**, load |
| `IouObjectTrackerTests` | stable ids for moving faces, separate tracks, gap tolerance (`MaxMissedAnalyses`), **small fast face sampled sparsely keeps its track** (prediction + distance fallback), fallback rejects far/different boxes, classes never share a track, `DetectionTrack` summaries |
| `RegionTrackingJobTests` | tracked path stored on the region and used for redaction; lost target trims the region |
| `RedactionPlannerTests`, `RedactionProcessorTests`, `RedactionRegionStoreTests` | pending/approved redacted, rejected not; padding within the frame; region frame ranges; degenerate boxes; clamped ROIs dispatched per effect; unknown effect is an error; copy-on-write snapshots and concurrent writers |
| `VideoExportServiceTests` | every frame in order, output appears only when complete, redactions on exactly the right frames, snapshot isolation, cancellation / writer failure leave no partial file, dropped frames detected, source can never be the output, missing folder rejected up front, `ExportJob` progress and cancellation |

### VisionRedact.IntegrationTests (55)

| Test class | Covers |
|---|---|
| `EmguVideoDecoderTests` | native runtime probe, container metadata, sequential decode with pixel checks, frames not overwritten by later reads, seek accuracy, `Skip`, missing/corrupt file translation, dispose, a real `PlaybackSession` played to the end with native frames back to baseline |
| `OnnxDetectorTests` | **UltraFace reproduces the model-zoo reference outputs**, preprocessing layout (letterbox, normalization, channel order), both models find nothing on a blank frame, detector info per contract, cancelled token, missing/corrupt/wrong-format models, catalog; optional `VR_FACE_IMAGE` photo |
| `GpuInferenceTests` | DXGI catalog lists the CPU first and hardware GPUs; **every GPU reproduces the model-zoo reference outputs** (within 3e-7 of the CPU); both detectors run on every GPU through the factory |
| `RedactionEffectTests` | each effect modifies only its region; solid black leaves no information; blur destroys fine detail |
| `ExportTests` | real encode: every frame exported, region blacked out, source file unchanged (SHA-256 before/after) |
| `FfmpegExportTests` (11) | export through `ffmpeg.exe`, checked with ffprobe: **H.264 output containing every frame**, redaction survives encoding (re-decoded centre pixel is black), **AAC audio present and its duration within 0.1 s of the video**, **a 0.5 s audio offset preserved** (measured with `silencedetect`), **cancel kills ffmpeg** (no orphaned process) and leaves no file, fallback to the OpenCV writer when ffmpeg.exe is missing, AVI stays on Motion-JPEG, exact frame-rate strings (`60000/1001`, `30000/1001`, `24000/1001`, `25`, `60`, `12.5`) |
| `ProjectPersistenceTests` | round trip of every field, 50,000 detections save + load < 5 s, corrupt/missing/future files, out-of-range boxes clamped on load, save → reopen → relink moved media |
| `TrackingAndSoakTests` | the KCF visual tracker follows a moving object in real frames; **long playback keeps native frames and memory bounded** (< 64 MB growth, live frames back to baseline) |

Helpers: `SyntheticVideo` (writes a small MJPEG/AVI with the real OpenCV `VideoWriter`; frame N is a known gray
level), `TensorProtoReader` (reads the model zoo's `.pb` reference tensors without a protobuf dependency),
`ModelFiles` + `RequiresModelFactAttribute` (skips with "run scripts/download-model.ps1" when a model is absent),
`FfmpegSourceVideo` (a 4 s 640×360 clip at 30000/1001 fps with a 1 kHz tone starting 0.5 s in, generated by FFmpeg at
test time), `RequiresFfmpegFactAttribute` (skips with "run scripts/download-ffmpeg.ps1" when FFmpeg is absent).

## Fakes

- `FakeVideoDecoder` (+ factory) — deterministic content (frame N has marker N), injectable blocking latency and
  failures, detects concurrent calls, records every frame it creates so tests can assert each was disposed.
- `TestVideoFrame` — backed by `NativeMemory`, so ownership bugs behave like real native bugs.
- `RecordingFrameSink` — records presented indices, verifies frames arrive undisposed, disposes them; tests `await`
  conditions (`WaitForFrameAsync`) instead of sleeping.
- `FakeObjectDetector` (+ factory) — deterministic boxes derived from the frame index, injectable latency and failures,
  call and concurrency tracking.
- `FakeVideoWriterFactory` — records written frames (index + first pixel byte) instead of encoding; can drop frames or
  fail, to test the export guarantees.
- `FakeExportService` — reports progress and honours cancellation, for `ExportJob` tests.

## Model conformance tests

A detector that runs without error can still be wrong (channel order, normalization, padding, output layout). So:

- The model zoo ships a reference input and the expected outputs for UltraFace; `scripts/download-model.ps1` fetches
  them (SHA-256 verified). The integration tests run our own `InferenceSession` on that input and compare with the
  reference, on the CPU and on every hardware GPU.
- Preprocessing is checked separately: letterbox offsets, pad value, `(x − mean) × scale`, RGB plane order.
- Contract validation is tested with deliberately wrong and corrupt models, so a bad model fails once with a clear
  message instead of producing garbage.

## Timing tests without flakiness

- Logic that depends on time (`PlaybackClock`, progress throttling) uses `FakeTimeProvider` — fully deterministic.
- End-to-end worker tests use the real clock but assert **ordering and state**, never exact durations; they wait
  on events with generous timeouts. The suite was run repeatedly to check for flakiness.

## Why integration tests generate their own video

No binary fixtures in the repo, no licensing questions, and the content is known: each frame is a solid gray level,
so seeking to frame 40 can be verified by reading a pixel. MJPEG/AVI is used because OpenCV encodes it without
external codecs.

## Benchmarks as a complement

Tests assert correctness and bounds; they do not measure speed. `tools/VisionRedact.Benchmarks` (Release build) does:
B1 skip vs decode for unsampled frames, B2 preprocessing allocation, B3 `Detect()` latency per model and device,
B4 end-to-end analysis throughput, B5 memory soak, B6 repository at 50,000 detections and export throughput,
B7 blur cost, B8 (`--tracking`) box accuracy between samples (hold vs interpolation, per tracker, against YOLO on every
frame). Results and before/after comparisons are in [Performance.md](Performance.md); raw output in
[benchmarks-baseline.txt](benchmarks-baseline.txt).

## Running

```bash
dotnet test VisionRedact.slnx
```

Model-dependent tests need `scripts/download-model.ps1` (UltraFace + reference data) and `models/yolo11n-face.onnx`;
GPU tests need a DirectX 12 GPU; FFmpeg export tests need `scripts/download-ffmpeg.ps1`. Without them those tests are
skipped, not failed (the OpenCV-fallback and frame-rate tests run without FFmpeg).
