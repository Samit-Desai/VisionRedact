# Memory Management

## Purpose

Memory must be bounded by *pipeline capacity*, never by *video length or file size*. A 20 GB file must not need
20 GB of RAM, and a two-hour playback session must end with the same number of live frames it had after one minute.

## Where memory lives

```text
Managed heap (GC-visible)             Native heap (GC-blind)
─────────────────────────             ────────────────────────────────────────────
MatVideoFrame        ~100 B   ──────► cv::Mat header + pixel buffer        ~6 MB @1080p BGR
EmguVideoDecoder     ~100 B   ──────► VideoCapture + FFmpeg contexts        tens of MB (one per pipeline)
InferenceSession     small    ──────► ONNX graph, weights, memory arenas   model-dependent (~150 MB observed in-app)
  + DirectML EP                ─────► D3D12 upload/readback heaps          private bytes rise to ~1 GB
LetterboxPreprocessor float[] 4.9 MB (640×640×3, pinned while wrapped)  + resize/canvas Mats (native)
OrtValue (per call)  small    ──────► output tensors (YOLO 168 KB)         freed at end of Detect()
Detection records    ~100 B each, managed only
```

The GC schedules collections based on managed allocation pressure. A frame looks like 100 bytes to it, so leaked
frames accumulate gigabytes of native memory before any finalizer runs. **Deterministic disposal is required, not
an optimization.** Emgu's and ONNX Runtime's finalizers are a safety net only.

## Ownership model

```text
EmguVideoDecoder.ReadNext()
   mat = new Mat()                      decoder owns
   capture.Read(mat)
   frame = new MatVideoFrame(mat)       ownership: mat → frame
   return frame  ───────────────────►   caller owns
                                          │
      ┌───────────────────────────┬───────┴──────────────────┬──────────────────────────┐
      ▼                           ▼                          ▼                          ▼
 PlaybackSession              AnalysisJob                VideoExportService        RegionTrackingJob /
 decode task → Channel(3)     sampled → WriteAsync        decode → Channel(4) →      FrameDetectionService
 → present task               (Channel(4)) → consumer     redact → Channel(4) →      using → Dispose
 → Present → sink owns        not sampled → Skip()        encode → Dispose; ≤ ~11
 or late → Dispose            (no frame is created)
 or stale generation → Dispose
      │
      ▼
 WriteableBitmapFramePresenter
 slot swap: stale frame → Dispose (dropped++)
 render: WritePixels → Dispose (finally)
```

Rules:

1. **Exactly one owner.** Handing a frame on is a transfer; the sender must not touch it again.
2. **Every owner disposes in `finally`** (or transfers). Failure paths included — `EmguVideoDecoder.ReadNext` disposes
   the Mat if wrapping fails; a producer whose `WriteAsync` throws disposes the frame it still owns;
   `PlaybackSession.Present` disposes the frame if the sink throws; buffered frames are drained and disposed when a
   session or job stops.
3. **Dispose is idempotent and race-safe** (`Interlocked.Exchange` flag) so defensive disposal is always safe.
4. **Never queue a buffer the producer reuses.** `VideoCapture.QueryFrame()` returns a Mat that the capture overwrites
   on the next grab; queuing it would silently corrupt downstream frames. We `Read` into a fresh Mat every time.
   Guarded by `Frames_are_independently_owned_and_not_overwritten_by_later_reads`.
5. **No stored spans.** `GetPixels()` returns a `ReadOnlySpan<byte>` over native memory — a ref struct that cannot be
   stored in a field or captured by a lambda, so it cannot outlive the frame by accident.
6. **Reuse inside one owner, never across owners.** The preprocessing buffers are reused because they never leave
   `OnnxObjectDetector`; decoded frames are not pooled because they cross threads.

## Resource ownership table

| Resource | Created by | Owned / disposed by |
|---|---|---|
| `Mat` (decoded frame) | `EmguVideoDecoder.ReadNext` | the `MatVideoFrame` wrapping it |
| `OwnedVideoFrame` | decoder | current holder; finally the presenter / consumer / export loop |
| `VideoCapture` | `EmguVideoDecoderFactory.Open` | `EmguVideoDecoder.Dispose` |
| `IVideoDecoder` (playback) | factory | `PlaybackSession.DisposeAsync` (after both tasks exit) |
| `IVideoDecoder` (analysis, export, tracking, detect-frame) | factory | the job / service, `using` |
| `PlaybackSession` | `VideoWorkspace.OpenAsync` | `VideoWorkspace` (on next open / close / shutdown) |
| `CancellationTokenSource` (session lifetime, job) | `PlaybackSession` / `ProcessingJob` | the same object |
| `FileStream` (access check) | factory | `using`, immediately |
| `VideoWriter` | `EmguVideoWriterFactory` (tries avc1 → mp4v → MJPG) | `EmguVideoWriter`, disposed by the export loop in `finally` |
| `ffmpeg.exe` `Process` | `FfmpegVideoWriterFactory.Create` | `FfmpegVideoWriter`: `Complete()` waits for exit; `Dispose` without it kills the process tree |
| Emgu `Tracker` (KCF/MIL/CSRT) | `EmguVisualTrackerFactory.Create` | `EmguVisualTracker`, disposed by `RegionTrackingJob` |
| `InferenceSession` | `OnnxDetectorFactory.Create` | `OnnxObjectDetector`; detectors cached by `DetectorHost`, disposed at shutdown after jobs are cancelled |
| `SessionOptions` | factory | `using` — only needed while the session is created |
| resize `Mat`, canvas `Mat`, `float[]` tensor | `LetterboxPreprocessor` (one per detector) | the detector's `Dispose` |
| input `OrtValue` (wraps the reused `float[]`), `RunOptions`, output `OrtValue`s | `OnnxObjectDetector.Detect` | `using` before `Detect` returns |
| detector handle given to a job | `DetectorHost.GetAsync` | the job disposes it; for a single model it is a non-owning wrapper (no-op), for several a composite — the cached sessions stay with `DetectorHost` |

## Bounding the numbers

- **Playback.** Live preview frames are bounded by `DecodeAheadFrames + 6` = 9 with the default buffer of 3: the
  decode-ahead channel's contents, the frame being decoded, the one-frame channel to the redaction task, the frame
  being redacted, the present task's held frame, the presenter's pending slot and the frame being copied on the UI
  thread (PlaybackSession's remarks). Measured on the 1080p59.94 AVCHD file: **4 live frames**.
- **Analysis.** Channel capacity 4 + the frame being decoded + the frame being analyzed. Measured: ≤ 6 live frames with a
  slow detector (unit test) and in the B4 pipeline benchmark.
- **Everything together.** Playback + analysis concurrently never exceeded **10** live native frames in any run.
- `OwnedVideoFrame.LiveInstances` exposes the process-wide count (diagnostics panel; asserted in tests).

Never done anywhere: `File.ReadAllBytes(video)`, `List<Mat> allFrames`, caching decoded frames per video.

## Allocation per frame

**Decoded frames.** Each `ReadNext` allocates a new native buffer. That keeps ownership trivial. Unsampled frames in
analysis use `Skip()` (grab without retrieve), so no buffer is allocated for them at all: native frame allocations fell
from 900 → 75 per benchmark run (B1), and throughput on the real file rose 272 → 615 frames/s. A frame pool
(return-to-pool on Dispose) remains a candidate only if a profile shows native allocator cost — none observed so far.

**Inference.** `LetterboxPreprocessor` allocates its resize target, padded canvas and 4.9 MB float tensor once per
detector. The input `OrtValue` wraps (pins) that array instead of copying it, and `UltraFaceDecoder` / `YoloDecoder`
read the outputs through spans over native memory instead of copying them (YOLO's output alone is 168 KB, a Large
Object Heap allocation per frame if copied). Measured (B2, 300 frames): **4.8 MB → 0.3 KB** managed allocation per frame,
**Gen2 collections 99 → 0**; full `Detect()` allocates 1.0–1.4 KB per call (B3). This matters for the whole app: a
Gen2 collection pauses every managed thread, including the UI thread.

**Blur.** Large regions are blurred at reduced resolution into a small temporary `Mat`; that is a speed optimization
(61.9 → 1.5 ms per region), not a memory one.

## ONNX Runtime and DirectML

- One `InferenceSession` per (model, device), created once and reused: a session holds the optimized graph, the weights
  and memory arenas; creating one per frame would cost hundreds of milliseconds and repeated native allocations.
- A session on the DirectML execution provider raises private bytes to **~1 GB** (GPU upload/readback heaps). That is a
  fixed cost per session, not growth; it is why sessions are cached per device and not duplicated per job.
- The DirectML EP requires `EnableMemoryPattern = false` and sequential execution; both are set by the factory.
- Switching device creates a separate cached session; a running job keeps the detector it started with.
- `ShellViewModel.ShutdownAsync` cancels jobs first, then disposes `DetectorHost` — never the other way round.

## Detection repository

`InMemoryDetectionRepository` holds immutable `Detection` records in a dictionary by id plus id lists per frame, and a
**bit set** of analyzed frames (1 bit per frame: a 2-hour 30 fps video needs 27 KB). `Changed` events carry one batch
per analyzed frame; the UI queues them and drains at 4 Hz, so nothing per-detection accumulates on the Dispatcher.
Measured with 50,000 detections: insert 20 ms total, display lookup 0.18 µs, full snapshot 6 ms. Project save/load of
the same 50,000 streams through a `FileStream` (no giant JSON string) and is asserted < 5 s by test.

## Soak results

- **B5** (playback session + UltraFace analysis on every frame, 5,400 frames, 6 passes, CPU EP): private bytes varied
  within **184–238 MB** and ended at 195 MB — no upward trend; live native frames ≤ 10.
- **Integration test** `Long_playback_keeps_native_frames_and_memory_bounded`: asserts < 64 MB growth after warm-up and
  that `LiveInstances` returns to its baseline.
- In the app, with a 4.3 GB file open, YOLO loaded and analysis running on the CPU: ~750 MB working set (ONNX Runtime
  arenas and the model, three FFmpeg decoder contexts, WPF). With a DirectML session, expect ~1 GB private bytes.

## Testing

- `TestVideoFrame` uses `NativeMemory.Alloc`/`Free` so fakes have real native semantics.
- `Every_decoded_frame_is_disposed_exactly_once_across_a_mixed_session`,
  `DisposeAsync_during_playback_stops_the_worker_and_releases_every_frame`, `Seek_flushes_buffered_frames_and_disposes_them`,
  `Cancellation_stops_both_sides_and_releases_every_frame`, `Inference_failure_fails_the_job_with_the_root_cause_and_releases_frames`
  and `Cancellation_leaves_no_partial_file_and_releases_frames` check that each created frame is released.
- `Producer_faster_than_consumer_keeps_live_frames_bounded` (playback) and
  `Producer_faster_than_consumer_is_held_back_by_the_bounded_channel` (analysis) assert the bounds.
- Integration tests assert `LiveInstances` returns to its baseline after real OpenCV decoding and playback, plus the
  soak test above.
