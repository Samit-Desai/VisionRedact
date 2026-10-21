# System Design

Status: **implemented** — playback, analysis and export pipelines, plus region tracking and single-frame detection,
all exist and are tested. Measured numbers: [Performance.md](Performance.md).

## Three pipelines over one file

```text
                         VIDEO FILE (never modified)
                              │
          ┌───────────────────┼───────────────────┐
          ▼                   ▼                   ▼
      PLAYBACK            ANALYSIS              EXPORT
   decoder #1           decoder #2           decoder #3
   latency-first        throughput-first     correctness-first
          │                   │                   │
   decode-ahead         bounded Channel       decode ‖ redact ‖ encode
   Channel(3) → WPF     → ONNX → repository   (2 bounded Channels) → FFmpeg
                                              (H.264 + audio) or OpenCV
```

| | Playback | Analysis | Export |
|---|---|---|---|
| Code | `PlaybackSession` | `AnalysisJob` | `VideoExportService` / `ExportJob` |
| Optimizes for | latency, smoothness, responsiveness | throughput, bounded memory | correctness, determinism |
| May drop frames? | **yes** — stale preview frames are worthless | yes, *by policy* (sampling), never by accident | **never** |
| Resolution | source (display scales) | model input (320×240 UltraFace, 640×640 YOLO) | source |
| Backpressure | decode-ahead `Channel(3, Wait)`; presenter keeps only the latest frame | block producer (`Channel(4, Wait)`) | none needed (single loop) |
| Cancellation | session dispose | job token, cooperative; `RunOptions.Terminate` inside inference | job token, temp file deleted |
| Threads | decode task + present task | producer task + consumer task | 1 pool thread |

Two smaller users of the same pattern — their own decoder, never playback's frames:

- **Region tracking** (`RegionTrackingJob`): decodes a manual region's frame range sequentially and runs a visual
  tracker (KCF) from its box; the resulting per-frame path is stored on the region.
- **Detect this frame** (`FrameDetectionService`): opens a decoder, seeks, decodes one frame, runs the selected
  models, stores the result.

### Why separate them

One shared decoder would force one set of trade-offs on three incompatible goals:

- Playback seeks constantly (scrubbing). If analysis shared the decoder, every seek would scramble the analysis
  cursor; if playback waited for analysis, the UI would freeze behind inference.
- Playback *must* drop frames when behind; export *must not*. A shared frame source makes "can this frame be
  dropped?" a question every consumer has to ask — and eventually someone gets it wrong.
- Export must never read preview frames: preview may be dropped, stale, or already redacted in place by the preview
  processor.

Cost: the file is decoded up to three times concurrently (CPU, disk I/O). That is acceptable because each pipeline is
independently bounded, and the alternative (a shared decode fan-out with per-consumer policies) is more complex and
couples their failure modes. Measured consequence: with GPU inference, analysis becomes decode-bound (queue 0/4, 726
decoder frames/s at 5 samples/s on the RTX 3500) — the next lever is hardware decode, not sharing decoders.

## Playback architecture

```text
UI thread                     decode task (only caller of the decoder)      present task
─────────                     ─────────────────────────────────────────      ─────────────
Play/Pause/Seek/Step ──►  mailbox: PositionRequest(generation, frame) via CAS, step counter, play flag
                              │                                              │
                              ▼                                              ▼
                   new generation? → decoder.Seek                 stale generation → dispose
                   ReadNext → RedactionPreviewProcessor            PlaybackClock (TimeProvider)
                   (preview redactions, in place)                  late? → drop (max 4 in a row,
                              │                                            never the last frame)
                              ▼                                    wait until due (interruptible)
                   Channel<DecodedFrame>(3, Wait) ───────────────► IPreviewFrameSink.Present(frame)
                                                                                  │ ownership moves
WriteableBitmapFramePresenter ◄───────────────────────────────────────────────────┘
   single-slot mailbox, stale frame disposed + counted
   │ one coalesced BeginInvoke (Render priority)
   ▼
WritePixels → dispose frame → FrameRendered(index) → PlayerViewModel position + overlays
```

Details: [VideoPipeline.md](VideoPipeline.md).

## AI analysis architecture

```text
AnalysisService.StartAsync(models, sampling)
   └─ DetectorHost.GetAsync(models)     one InferenceSession per (model, device), cached, reused
   └─ ProcessingJobManager.Start(AnalysisJob)

decoder #2 (producer task, sequential)
      │  IFrameSamplingStrategy.ShouldAnalyze(index)?
      ├── no  → decoder.Skip()           (grab, no pixel conversion, no 6 MB buffer)
      └── yes → ReadNext → Channel<OwnedVideoFrame>  Bounded(4, FullMode = Wait)   ← backpressure
                                      │
                                      ▼
consumer task: IObjectDetector.Detect(frame, ct)
                 LetterboxPreprocessor (reused buffers) → InferenceSession.Run (CPU or DirectML)
                 → UltraFaceDecoder / YoloDecoder (spans over native output) → NMS per class
               → IouObjectTracker.Assign (TrackId)
               → IDetectionRepository.AddFrameResults     finally: frame.Dispose()
                                      │
                                      ▼
DetectionsViewModel: ConcurrentQueue of change batches, drained at 4 Hz on the UI thread
PlayerViewModel: overlay/marker refresh on FrameRendered and a 2 Hz repository.Version check
```

Why bounded: decoding runs at hundreds of frames/s while YOLO on the CPU consumes ~40 (B4: 39 frames/s), so an
unbounded queue would grow by hundreds of 6 MB frames per second and exhaust memory within seconds. A bounded channel with `FullMode.Wait` makes the
producer's `WriteAsync` wait, so the decoder runs at the consumer's pace: memory is `capacity × frame size`,
independent of video length. `DropOldest` is wrong here: analysis silently skipping frames must be a sampling decision,
not a side effect of load. Measured: ≤ 6 live frames in the pipeline benchmark; ≤ 10 live frames with playback and
analysis running together.

Cancellation: cooperative between frames in both tasks; inside inference the token sets `RunOptions.Terminate`, which
ONNX Runtime checks between graph nodes. Frames still in the channel are drained and disposed in `finally`. If one
task fails, it cancels the other through a linked token, and the job reports the **root cause**, not the secondary
`OperationCanceledException`.

Between analyzed frames, `InMemoryDetectionRepository.GetForDisplay` either holds the last sampled boxes or
interpolates each track between its two surrounding analyzed frames. Measured on the real file (B8, 1-in-12
sampling): interpolation + prediction + distance fallback covers 83.0 % of ground-truth faces with 1.0 px median centre
error, against 78.8 % / 2.5 px for holding.

Details: [AiPipeline.md](AiPipeline.md).

## Export architecture

```text
RedactionSnapshot.Capture(detections, regions, settings)     immutable copy of the review
   ▼
ExportJob → VideoExportService (decode ‖ redact ‖ encode: three tasks, two bounded channels, still in order)
   decoder #3: ReadNext from frame 0 — never Seek, never Skip
   plan = RedactionPlanner (same planner as preview; rejected detections excluded)
   IRedactionProcessor.Apply(frame, plan)     blur / pixelate / black, clamped ROI
   IVideoWriter.Write(frame)                  MP4: FfmpegVideoWriter (ffmpeg.exe, H.264 + AAC audio)
                                              fallback / AVI: EmguVideoWriter (avc1 → mp4v · MJPG)
   IVideoWriter.Complete()                    flush, close the file, surface encoder errors
   ▼
frames written == frames read?  → temp file ──File.Move──► requested path (after the check)
                          no    → IncompleteOutput, temp file deleted
```

No frame is ever skipped. The source file is opened read-only and the output path must differ from the input.
With the optional FFmpeg build installed, MP4 export runs through an external `ffmpeg.exe`: the best working hardware
H.264 encoder and the source's audio (aligned, AAC) — 147–167 frames/s at 1080p59.94 against 72–80 with OpenCV's
`VideoWriter`, which remains the fallback (video only). Details: [ExportPipeline.md](ExportPipeline.md).

## Persistence

`ProjectService` saves the review (detections with status and track ids, analyzed-frame set, hold frames, manual
regions with tracked paths, settings, a source fingerprint) through `IProjectRepository`. `JsonProjectRepository`
writes versioned JSON DTOs (`schemaVersion` 1) to a temp file and renames it. On open, a missing source triggers a
relink; a relinked file whose fingerprint (size, resolution, frame count, fps) differs is reported as changed.

## Threading model (summary)

| Thread | Does | Never does |
|---|---|---|
| UI (Dispatcher) | bindings, layout, WritePixels (one memcpy/frame), commands, 4 Hz list flush, 2 Hz overlay tick, diagnostics sampling | decode, file I/O, inference, waiting on tasks |
| Playback decode task | decode, seek, skip frames it can't deliver in time | touch WPF objects |
| Playback redaction task | preview redaction, overlapping the next decode | touch WPF objects |
| Playback present task | pacing, dropping, handing frames to the presenter | touch WPF objects, call the decoder |
| Analysis producer / consumer | decode / inference + tracking + repository writes | touch WPF objects, share a decoder |
| Export, region tracking | sequential decode → process | touch WPF objects |
| Thread pool (`Task.Run` at use-case boundaries) | opening files, model loading, native runtime probe, single-frame detection | — |

Details: [ThreadingModel.md](ThreadingModel.md).

## Native frame ownership (summary)

```text
decoder: new Mat → Read into it → wrap in MatVideoFrame      (decoder owns)
   ── return ──►  caller owns  ── WriteAsync / Present ──► next stage owns
                                                       └─► try/finally Dispose (exactly once)
```

Details: [MemoryManagement.md](MemoryManagement.md), [ManagedNativeInterop.md](ManagedNativeInterop.md).

## Most important decisions & trade-offs

| Decision | Benefit | Cost |
|---|---|---|
| Separate playback / analysis / export decoders (ADR-0006, 0007) | independent policies and failure modes | up to 3× decode work |
| Explicit `OwnedVideoFrame` ownership (ADR-0008) | bounded native memory, no GC dependence | discipline: every path needs try/finally |
| Preview drops stale frames (ADR-0009) | UI never falls behind | visual stutter under load (counted, shown in diagnostics) |
| Bounded channels, `Wait` mode (ADR-0005) | memory independent of video length | producer throttled to consumer speed |
| Synchronous `IVideoDecoder` | honest about blocking native work | callers must choose a thread |
| Anchor-based clock on `TimeProvider` | no drift; deterministic tests | Windows timer granularity still adds ~15 ms jitter |
| Same `RedactionPlanner` for preview and export | what you review is what you export | preview redaction costs CPU on every shown frame; on its own task it overlaps decoding, so per frame the slower of the two sets the pace (both shown in Diagnostics) |
| DirectML instead of CUDA | one build for NVIDIA/AMD/Intel, no CUDA install | ONNX Runtime DirectML package ends at 1.24.4; ~1 GB private bytes |
| Sampling + track interpolation | 5 samples/s on a 59.94 fps file runs inference on 1 frame in ~12 | faces the detector misses on a sampled frame stay uncovered (~17 % in B8) |
| CFR assumption via OpenCV | simple frame↔time mapping | VFR files may drift (ADR-0018) |
| Clean Architecture, pragmatic (ADR-0001) | testable core, replaceable adapters | more projects; resisted interface-per-class |
