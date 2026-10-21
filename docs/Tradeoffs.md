# Trade-offs

Every significant decision, what it cost, and when it should be revisited. Details in [ADRs](adr/README.md).

| Decision | Gain | Cost / risk | Revisit when |
|---|---|---|---|
| One decoder per pipeline (playback, analysis, export, storyboard, exact preview frames, detect-this-frame, people, region tracking) | independent policies (drop vs never drop), no shared failure, no locking | the same file is decoded once per pipeline running at the time (typically playback plus one job) | profiling shows decode dominates with several pipelines active |
| Synchronous `IVideoDecoder` | honest about blocking native work, no fake async | callers must choose a thread | an async-native decoder (e.g. MF source reader callbacks) is introduced |
| Explicit frame ownership | bounded native memory, measurable leaks | every path needs try/finally; fan-out needs copies | a frame must be shared by many consumers → ref-counted variant |
| Decode-ahead channel (3) in playback | absorbs keyframe jitter | +3 frames memory, extra latency after a seek (flushed by generations) | 4K/8K sources (≈100 MB per 3 frames) |
| Preview drops stale frames | real-time preview under load | visible stutter on slow machines | reviewers need guaranteed every-frame review → an explicit step-review mode |
| Bounded analysis channel (4, Wait) | memory independent of length, backpressure | producer throttled to inference | GPU inference makes decode the bottleneck (it did: queue 0/4) → hardware decode |
| Sampling + hold | 5–12× faster analysis | short-lived faces missed; moving faces can leave a held box | strict "every face" requirements → every-frame + tracking |
| Fail-safe review (pending = redacted) | a missed review never leaks a face | reviewers must reject false positives | workflows that require explicit approval before export |
| Normalized coordinates | one model for AI, UI, export; DPI/resize safe | conversions at every edge | never, in practice |
| `LetterboxTransform` shared by AI and UI | one tested implementation | — | — |
| UltraFace + YOLO, pluggable | speed vs recall choice; new classes by dropping a model | YOLO weights carry AGPL-3.0 | commercial distribution |
| ONNX Runtime DirectML build (1.24.4) | GPU on NVIDIA/AMD/Intel, no CUDA install; 5× YOLO | 1.24.4 is the latest DirectML release while the CPU package has moved on (1.31 in October 2026); DirectML in sustained engineering | Windows ML / newer EP becomes the supported path |
| OpenCV for decoding (and as the export fallback) | one native stack for decode, fast delivery; export works without FFmpeg | VFR approximations, no HW decode, mid-GOP seek artefacts in MPEG-TS; the fallback writer has no audio and no HW encode | evidentiary timestamps or hardware decode required → FFmpeg decoder behind `IVideoDecoderFactory` (export already moved: next row) |
| FFmpeg as an external process for MP4 export (`FfmpegVideoWriter`) | 2× faster export (72–80 → 147–167 frames/s), files under a third of the size at the chosen bitrate, audio kept (AAC); crash/memory isolation; no binding layer; LGPL binaries unmodified; one new class behind `IVideoWriterFactory` | optional 73 MB download (about 180 MB unpacked) to manage; one pipe copy per frame; process lifecycle (kill on abort, exit code, stderr parsing); encoder chosen by probe, so output codec varies by machine; subtitles not carried | per-frame pipe copy shows up in a profile, or frame-accurate remux of untouched GOPs (smart render) is needed → libav\* in process |
| Export snapshot | deterministic output; review can continue | snapshot memory (≈ size of detections) | — |
| Reduced-resolution blur | 41× faster blur, 5.7× faster export | not bit-identical to a wide Gaussian | a policy requiring a specific blur definition |
| KCF visual tracker (CSRT unusable in Emgu 4.13) | reliable, 21 ms/frame | less accurate than CSRT on scale change | after an Emgu upgrade, re-run the tracker probe |
| IoU tracker with constant-velocity prediction + distance fallback | simple, deterministic, testable; keeps small fast faces on one track (262 → 80 tracks/min) | no appearance model: identity swaps in crowds; distance gate could join two nearby similar faces | crowded scenes → appearance embeddings (DeepSORT/ByteTrack-style) |
| Track interpolation between samples | boxes follow faces; visible drift 17.9 % → 4.3 % (B8) | linear motion assumed over one interval | fast direction changes matter → denser sampling or optical flow |
| Prism 8 (MIT) | no licence friction | no `AsyncDelegateCommand` (own `AsyncCommand`) | team standardizes on Prism 9 |
| In-memory detection repository | O(1)/O(hold) lookups, simple | whole set in RAM (~150 B per detection) | millions of detections → SQLite with a frame index |
| JSON projects | human-readable, versioned, streaming | slower than binary for millions of rows | project files > 100 MB |
| Warnings as errors | analyzers can't be ignored | occasional friction | never |
| Pipelined export (3 tasks, 2 bounded channels) | 1.4× on real footage (52 → 74 frames/s H.264) | ~11 frames buffered (~66 MB), more threads; was still encoder-bound with OpenCV | done: FFmpeg writer with a faster encoder (row above) |
