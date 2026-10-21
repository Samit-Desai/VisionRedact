# Performance

All numbers below were **measured** with `tools/VisionRedact.Benchmarks` (Release build) on:

| | |
|---|---|
| CPU | Intel Core Ultra 9 185H (16 cores / 22 threads) |
| RAM | 64 GB |
| GPUs | NVIDIA RTX 3500 Ada Laptop (11.7 GB) and Intel Arc Pro (integrated), via DirectML |
| Runtime | .NET 10.0.12, x64 |
| Sources | synthetic 1920×1080 MJPEG (900 frames) and a real 1920×1080 59.94 fps AVCHD H.264 file (4.3 GB) |

Raw output: [`benchmarks-baseline.txt`](benchmarks-baseline.txt) — the latest full run (2026-09-24, B1–B8). Figures in
this document come from dedicated runs per optimization; the latest run agrees within ~10 % (e.g. YOLO CPU 24.9 ms,
RTX 5.3 ms; blur 1.53 ms; export 73 frames/s — that run predates the pipelined export of optimization 6, whose numbers come from `--export`), with two differences worth knowing: the YOLO GPU pipeline reached
179 frames/s, and the soak (B5) reports 254–309 MB because that process had also loaded DirectML sessions in B3/B4 — the
184–238 MB figure below is a CPU-only process. Timings vary with machine load: a run made while builds were in progress
was ~1.5× slower across the board, with the same ratios. Reproduce:

```bash
dotnet run -c Release --project tools/VisionRedact.Benchmarks -- "D:\path\to\real-video.mts"
```

Nothing in this document is claimed without a measurement; where a number came from the running app (diagnostics
panel) rather than the harness, it says so.

## Summary

| Area | Measurement |
|---|---|
| Playback, 1080p59.94 AVCHD | 59.9 decode FPS, 61.9 presented FPS, 3.8 ms average decode, 0 late drops, 4 live frames (diagnostics panel) |
| Detect() latency, UltraFace RFB-320 | CPU p50 5.5 ms · Arc 3.7 ms · **RTX 3500 2.4 ms**; 1.0 KB managed allocation per call |
| Detect() latency, YOLO11n face 640 | CPU p50 23.4 ms · Arc 14.2 ms · **RTX 3500 4.7 ms (5×)**; 1.4 KB managed allocation per call |
| Analysis pipeline, YOLO, every frame | CPU 39 frames/s · **RTX 3500 152 frames/s (3.9×)** |
| Analysis pipeline, every frame (first CPU-only run, before the GPU build) | UltraFace 155 frames/s, YOLO 46 frames/s; channel full at 4/4 (backpressure); ≤ 6 live frames |
| Analysis on the real file, 5 samples/s | CPU 29.9 AI frames/s (queue 4/4) · **RTX 3500 58.5 AI frames/s, 726 decoder frames/s (queue 0/4: decode-bound)** — diagnostics panel |
| Memory soak (playback + analysis, 5,400 frames) | private bytes after warm-up 184–238 MB, last 195 MB; ≤ 10 live native frames |
| Detection repository, 50,000 detections | insert 20 ms total; display lookup 0.18 µs; full snapshot 6 ms |
| Project save + load, 50,000 detections | < 5 s asserted by test (typically well under 1 s) |
| Export 1080p, blur on every frame | real MTS: **147–167 frames/s via FFmpeg (h264_mf — Microsoft's software H.264 encoder — + AAC audio, 45 MB)**; OpenCV writer 72–86 frames/s (avc1/mp4v, no audio, 148 MB) — optimizations 3, 6, 7 |
| Box accuracy between samples (B8, 1-in-12 sampling) | visible drift 17.9 % → **4.3 %**, coverage 78.8 % → 83.0 %, tracks 262 → 80 per minute — optimization 5 |
| OpenCV thread pool, 4K60 playback with preview redactions (app) | OpenCV's default 22 threads: 50–52 fps shown, 56–58 late frames, 14.4–14.6 cores · **4 threads: 60.2 fps, 0 late, 5.6–5.9 cores**; 4K60 export 39–41 → 48 frames/s — optimization 8 |

## Optimization 1 — skip unsampled frames without decoding pixels

**Problem.** Analysis at 5 samples/s on a 59.94 fps source discards 11 of every 12 decoded frames. The first
implementation decoded every frame (`ReadNext`) and disposed the unsampled ones.

**Baseline** (B1, 1 of 12 frames analyzed): synthetic 198 frames/s, real MTS 272 frames/s; 900 / 1,800 native frame
allocations (6 MB each at 1080p).

**Evidence.** Per-frame cost is split between `grab` (demux + decode) and `retrieve` (YUV→BGR conversion and a new
6 MB buffer). Counting native frame allocations showed that 92 % of them were for frames that were immediately
thrown away.

**Change.** `IVideoDecoder.Skip()` → `VideoCapture.Grab()` for unsampled frames: the frame is decoded (it must be,
for later frames), but not converted or copied.

**New measurement.** Synthetic 452 frames/s (**2.3×**), real MTS 615 frames/s (**2.3×**); native frame allocations
drop from 900 → 75 and 1,800 → 150.

**Trade-off.** None functionally; `Skip` exists only on our port, so a decoder implementation without a cheap skip
would fall back to `ReadNext` + dispose.

## Optimization 2 — reuse preprocessing buffers

**Problem.** Converting a frame to a 640×640×3 float tensor needs a resize target, a padded canvas and a 4.9 MB
float buffer. Allocating them per frame puts every tensor on the Large Object Heap.

**Baseline** (B2, 300 frames, allocate per frame): 4,800 KB managed allocation per frame; **99 Gen0 / 99 Gen1 / 99 Gen2
collections** per 300 frames; 2.95 ms per frame.

**Evidence.** `GC.GetAllocatedBytesForCurrentThread` and `GC.CollectionCount` (the same counters dotnet-counters
shows as *Allocation Rate* and *Gen 2 GC Count*): one blocking Gen2 GC every ~3 frames — during analysis *and* playback,
because a Gen2 collection pauses every managed thread, including the UI thread.

**Change.** `LetterboxPreprocessor` owns its buffers for the detector's lifetime; `OrtValue.CreateTensorValueFromMemory`
wraps (pins) the reused float array instead of copying it. Output decoders read ONNX outputs through spans over
native memory instead of copying them to arrays (YOLO's output alone is 168 KB — another LOH allocation per frame).

**New measurement.** 0.3 KB allocated per frame, **0 Gen0/Gen1/Gen2 collections** per 300 frames, 2.09 ms per frame
(**29 % faster**); full `Detect()` allocates 1.0–1.4 KB per call (B3).

**Trade-off.** The detector is no longer re-entrant (a lock serializes calls). Parallelism comes from ONNX Runtime's
intra-op threads instead, which the benchmark shows is sufficient (46–193 inferences/s).

## Optimization 3 — blur large regions at reduced resolution

**Problem.** 1080p export with one blurred region per frame ran at **13 frames/s** (B6 before) — a 23-minute video would
take 1.8 hours to export.

**Baseline** (B7): Gaussian blur of a 576×324 region with a kernel of ~⅔ of its short side, two passes: **61.9 ms per
region**.

**Evidence.** Export per-frame time ≈ decode (~4 ms) + encode (~8 ms) + blur (~62 ms): the effect was 80 % of the frame
time. Cost scales with pixels × kernel size, and the kernel scales with the region, so big regions are quadratically
expensive.

**Change.** Regions whose short side is > 64 px are downscaled to 48 px (area averaging), blurred with a proportional
kernel, and upscaled back (bilinear). Small regions keep the direct path.

**New measurement.** **1.5 ms per region (41×)**; export 75 frames/s MP4 / 64 frames/s AVI (**5.7×**).

**Trade-off.** The result is a slightly different low-pass (area-average + blur + bilinear instead of one wide
Gaussian). For anonymization this is at least as strong — detail below 1/48 of the region is gone either way — but
it is not bit-identical to the old output. `GaussianBlurEffect.DirectBlur` keeps the old path for comparison. Tests
assert that fine detail is destroyed and nothing outside the region changes on the direct path (small regions); the
reduced-resolution path has no such test yet.

## Optimization 4 — GPU inference through DirectML

**Problem.** YOLO11n on the CPU (23.4 ms p50) limits every-frame analysis to 39 frames/s while a discrete GPU sits idle.

**Baseline** (B3/B4, CPU execution provider): YOLO 23.4 ms p50 / 26.4 ms p95; pipeline 39 frames/s, channel full (4/4).

**Evidence.** Channel occupancy pinned at capacity = the consumer (inference) is the bottleneck; ONNX Run is ~80 % of
`Detect()` time (preprocess 2.1 ms of 23 ms).

**Change.** ONNX Runtime DirectML build; `DxgiAdapterCatalog` enumerates GPUs the way DirectML indexes them; the
factory appends the DirectML execution provider for the chosen adapter (memory pattern off, sequential execution) and
falls back to CPU if a session cannot be created. A "Run on" picker defaults to the GPU with the most dedicated memory.
Correctness: an integration test checks both GPUs against the model-zoo reference outputs (tolerance 2×10⁻²).

**New measurement.** YOLO **4.7 ms p50** on the RTX 3500 (5×), 14.2 ms on the integrated Arc; pipeline **152 frames/s**
(3.9×). With UltraFace on the GPU the channel is **empty** (0/4): inference outpaces decoding, so the bottleneck moved
to the decoder — the same bounded pipeline, now producer-bound.

**Trade-off.** The DirectML package's latest release is 1.24.4 (the CPU-only package is at 1.31): DirectML is in
sustained engineering and Microsoft steers new work to Windows ML. Private bytes rise to ~1 GB with a DirectML session
(GPU staging/upload heaps). First inference after load is slower (shader compilation) — warm-up is excluded from p50.
Next lever: hardware video decode (D3D11VA/NVDEC) so the GPU also takes the decode that is now the limit.

## Optimization 5 — boxes that follow the face between samples

This one is about accuracy rather than speed, but it follows the same method: a user-visible problem, a measurement,
a change, a new measurement.

**Problem.** At 5 samples/s on 59.94 fps footage only every ~12th frame is analyzed. The repository *held* each
analyzed frame's boxes for the next 11 frames, so a box stayed still while the player moved, then jumped at the next
sample. Reported by the user from the preview; the same lookup drives export, so a moving face could slide partly out of
its blur.

**Baseline** (B8: the first 3,600 frames of a real 1080p59.94 MTS; ground truth = YOLO11n on **every** frame, 9,963
faces; the app's 1-in-12 sampling replayed; each face on a non-analyzed frame scored against the box displayed for it):
**78.8 %** of faces covered by a box (+20 % padding), **17.9 %** visibly off (centre > 8 px from the face, among faces
with a box within 100 px).

**Evidence.** Two hypotheses. (1) Frame numbering off after seeks — ruled out: 5 of 5 seeks on the MTS delivered
exactly the frame a sequential decode produces (pixel difference 0.00). (2) The hold — confirmed by the benchmark.
Interpolating along tracks alone moved visible drift only to 13.8 %: the IoU tracker (with motion prediction, before the distance fallback) broke tracks for ~30 px faces
that move half their width between samples (262 tracks in one minute), so there was nothing to interpolate *to*.

**Change.** `GetForDisplay` interpolates each box linearly towards the same track's box on the next analyzed frame
(boxes without a match are still held, so redaction never shrinks). `IouObjectTracker` compares against a
constant-velocity *predicted* box and falls back to a size-gated centre-distance match (≤ 1.5 box sizes, area ratio
0.4–2.5).

**New measurement.**

| method | tracks | covered | centre error p50 | visible drift > 8 px |
|---|---|---|---|---|
| hold (before) | — | 78.8 % | 2.5 px | 17.9 % |
| interpolate, IoU + prediction | 262 | 78.9 % | 1.1 px | 13.8 % |
| interpolate, IoU + prediction + distance (current) | **80** | **83.0 %** | **1.0 px** | **4.3 %** |

**Trade-off.** Linear motion over ⅕ s; a sudden change of direction can still misplace a box briefly. The distance
fallback could join two nearby faces of similar size (no appearance model). The remaining ~17 % of uncovered faces are
detector misses on the analyzed frame, not drift; next levers are bridging short track gaps or denser sampling (cheap
on the GPU). Reproduce with `--tracking` (B8).

## Optimization 6 — pipelined export (decode ‖ redact ‖ encode)

**Problem.** Exporting the 23-minute 1080p59.94 MTS (82,373 frames) took about half an hour — longer than the video.
Reported by the user; the in-app export (under the debugger, H.264) ran at ~40 frames/s.

**Baseline** (`--export`, sequential loop: decode → redact → encode per frame, 1,800 real MTS frames with a blurred
region on every frame): **52 frames/s** with H.264 (`avc1`, OpenH264), **60 frames/s** with MPEG-4 (`mp4v`).

**Evidence.** Stage costs measured separately on 300 real frames: decode ~2.6–4.7 ms, blur ~1.5 ms per region, encode
10–13 ms (mp4v 98 frames/s, avc1 75–79 frames/s encode-only). A sequential loop pays their *sum* per frame; the stages
are independent blocking native calls, so they can overlap. Hardware encode is not available in this OpenCV build (every
`VIDEOWRITER_PROP_HW_ACCELERATION` request failed to open), and hardware decode was slower (120 vs 386 frames/s,
GPU→CPU copies).

**Change.** `VideoExportService` runs three tasks — decode, redact, encode — joined by two bounded channels
(capacity 4, `FullMode.Wait`, single reader/writer so order is preserved). Ownership, root-cause errors and draining on
cancel follow the analysis pipeline. At most ~11 frames are in flight.

**New measurement.**

| source | codec | sequential | pipelined |
|---|---|---|---|
| real 1080p59.94 MTS (1,800 frames) | avc1 (OpenH264) | 52 frames/s | **74 frames/s (1.4×)** |
| real 1080p59.94 MTS (1,800 frames) | mp4v | 60 frames/s | **86 frames/s (1.4×)** |
| synthetic 1080p (900 frames) | mp4v | 73 frames/s | **122–132 frames/s (1.7×)** |
| synthetic 1080p (900 frames) | MJPG (AVI) | 57 frames/s | **89–98 frames/s (1.6×)** |

Whole 23-minute file: ~26 → ~19 minutes with H.264, ~23 → ~16 minutes with mp4v.

**Trade-off.** ~11 buffered frames (~66 MB) instead of one; three threads instead of one. The encoder is now the
bottleneck (OpenH264, the only H.264 encoder OpenCV's writer offers here). Next levers: an FFmpeg adapter with NVENC/QSV/AMF
hardware encoding, or segment-parallel encoding (several encoders on different GOP ranges, then concatenated).

## Optimization 7 — FFmpeg writer: a faster H.264 encoder and audio

**Problem.** After optimization 6 the export was encoder-bound: OpenCV's writer only has OpenH264 (CPU, ~75 frames/s at
1080p), no audio, no bitrate control (≈48 Mbit/s files).

**Baseline** (`--export`, first 1,800 real MTS frames, blur on every frame, pipelined): OpenCV writer **72–80 frames/s**,
**148 MB**, no audio.

**Evidence.** OpenCV's bundled FFmpeg has no hardware encoders (every hardware-acceleration request failed to open).
A standalone FFmpeg 9.0 LGPL build on the same machine: `h264_mf` (as FFmpeg calls it by default: Microsoft's software
"H264 Encoder MFT", multi-threaded on the CPU) 421 frames/s on the real file, `h264_qsv` (Intel Quick Sync, hardware) 313, NVENC unavailable (driver offers NVENC API 13.0, FFmpeg 9 needs 13.1); BGR→NV12 conversion
631 frames/s — so an FFmpeg writer would move the limit well above the rest of the pipeline.

**Change.** `FfmpegVideoWriterFactory` streams raw frames to `ffmpeg.exe` (probing NVENC → Media Foundation → QSV →
AMF → OpenH264 at the time; Media Foundation first since 2026-10-11, see below), copies the source audio aligned to the first frame (AAC), and falls back to OpenCV when FFmpeg is
absent ([ADR-0020](adr/0020-ffmpeg-exporter.md)). On the reference laptop the probe selects `h264_mf`: a software
encoder (see the correction below).

**New measurement.** **147–167 frames/s, 45 MB, with audio** — ≈2× faster, files under a third of the size at the chosen bitrate (picture quality not measured). The whole 23-minute file
exports in ≈9 minutes instead of ≈19 (≈26 before optimization 6).

**Found along the way.** The first FFmpeg run measured only 49 frames/s: a 1,800-frame export still waited for FFmpeg
to read the source's entire 23-minute audio track. `apad` + `-shortest` makes the audio end with the video
(also correct for full exports whose audio is slightly longer or shorter) — 167 frames/s. Forcing Media Foundation's
`-hw_encoding 1`, which loads Intel's Quick Sync hardware MFT, was *slower* standalone (339 vs 421 frames/s): on this
22-thread CPU the software encoder outruns the integrated GPU's encode engine, at the cost of CPU cores.

**Correction and verification (2026-10-11).** This section first called `h264_mf` a hardware encoder. As the app
calls it (FFmpeg's default `-hw_encoding 0`), Media Foundation loads Microsoft's **software** "H264 Encoder MFT", which
runs on CPU cores; the speed-up over OpenH264 is a much faster, multi-threaded software encoder, not hardware. Verified
with the app's FFmpeg 9.0.2 (300- and 900-frame 60 fps test patterns, FFmpeg's verbose log naming the MFT, Windows'
per-process GPU engine counters; ffmpeg CPU includes ~1–2 cores for generating the pattern):

| Encoder | Works on the reference laptop | 4K60 test pattern | ffmpeg CPU | GPU engine busy |
|---|---|---|---|---|
| `h264_nvenc` (RTX 3500) | no: "required nvenc API 13.1, found 13.0; minimum driver 610.00" (installed 596.41) | – | – | – |
| `h264_mf`, as the app calls it | yes: "H264 Encoder MFT" (Microsoft, software) | 137 frames/s | 8.5 cores | none |
| `h264_mf -hw_encoding 1` | yes: "Intel Quick Sync Video H.264 Encoder MFT" | 115 frames/s | 1.8 cores | Intel Arc media engine, avg 50 % |
| `h264_qsv` (Intel Quick Sync) | yes | 92 frames/s | 1.7 cores | Intel Arc media engine, avg 50 % |
| `libopenh264` | yes (software; about one core as called) | 62 frames/s | 1.3 cores | none |
| `h264_amf` | no AMD GPU | – | – | – |

At the time of this table (driver 596.41) hardware H.264 was supported but not used: NVENC needed a newer NVIDIA
driver, and Quick Sync works but comes after `h264_mf` in the probe order, which succeeds first. After the NVIDIA driver update to 610.88 (2026-10-11), NVENC passes the probe and, first in the probe order, was selected automatically. Inside the export pipeline it was not faster: 17–20 % slower than `h264_mf` at 1080p, about 6 % slower at 4K30, within noise at 4K60 (measured, PC in use; docs/blog/_research/measurements.md, "NVENC after the driver update"). So `h264_mf` now comes first in the probe order. On this 22-thread CPU the software encoder is faster both in
isolation and inside the pipeline; a hardware encoder would free ~4–7 CPU cores, which matters only if decode and blur
run short of CPU. To confirm on an idle PC.

**Trade-off.** A second native stack (~180 MB, downloaded, not committed) and an external process to manage (killed on
cancel, exit code checked). Encoder choice depends on drivers and on the probe order: Media Foundation comes first
(measured faster than NVENC at 1080p, see above), so NVENC and Quick Sync are reached only when it fails.

## Optimization 8 — cap OpenCV's thread pool at 4

Measured on a freshly restarted, idle PC (2026-10-11; Visual Studio and other heavy apps closed, CPU 3–5 % before
starting, AC power, High performance plan). Each thread count runs in its own process; app runs in mirrored order
(4, 1, 22, 22, 1, 4), two runs each.

**Problem.** 4K60 playback with preview redactions showed 50–52 of 60 fps with 56–58 late frames per ~10 s, and the app
used 14.4–14.6 of the CPU's 22 threads — while decoding and redacting a frame took less than one frame interval.

**Baseline** (OpenCV's default pool: one thread per CPU, 22). App, 4K60 crowd clip, scanned, preview redactions on:
50.3–51.9 fps shown, 56–58 late frames, 14.4–14.6 cores, decode 18.6–19.1 ms, `WritePixels` p95 9.0–11.9 ms.

**Evidence.**

*Where the CPU goes.* Every thread's CPU time over 6 s of that playback, attributed to the library that created the
thread (its Win32 start address):

| OpenCV threads | App total | OpenCV pool (CONCRT140.dll) | FFmpeg decode (msvcrt.dll) | .NET tasks, GC (coreclr) | WPF render + UI thread |
|---|---|---|---|---|---|
| 22 (default) | 15.4 cores | 22 threads, all busy: **9.83 cores** | 16 threads: 2.62 | 1.87 | 0.71 + 0.33 |
| 4 | 6.38 cores | 4 threads: **1.50 cores** | 2.33 | 1.61 | 0.46 + 0.44 |
| 1 | 4.29 cores | **none** (OpenCV runs on the calling thread) | 1.91 | 1.49 | 0.56 + 0.25 |

Emgu's OpenCV build parallelizes with Microsoft's Concurrency Runtime (`Parallel framework: Concurrency`). Two-thirds of
the app's CPU was its 22 worker threads, for the same frames and identical pixels.

*Why: tiny jobs versus whole frames.* A standalone test of exactly the steps `GaussianBlurEffect` runs per face (area
resize to 48×48, two 31×31 Gaussian passes, linear resize back over a 300×300 box), 12 faces per frame, two runs:

| OpenCV threads | 12 faces: wall / CPU | CPU 50 ms after the burst | Whole 4K frame, 5×5 blur: wall | 4K → 640×360 resize: wall |
|---|---|---|---|---|
| 1 | **7.0–7.5 / 7.1–7.5 ms** (1 core) | 0.02–0.05 cores | 32–34 ms | 0.44 ms |
| 2 | 9.7–10.5 / 14–15 ms | 0.00–0.01 | 33–35 ms | 0.35–0.38 ms |
| 4 | 8.4–8.5 / 22–24 ms | 0.00–0.02 | 15–16 ms | 0.23–0.25 ms |
| 8 | 8.8–9.3 / 46–49 ms | 0.00–0.01 | 9.0–9.4 ms | 0.16–0.19 ms |
| 22 | 11.5–12.3 / **147–162 ms** (13 cores) | 0.19–0.30 | **5.3 ms** | 0.19–0.21 ms |

Since optimization 3 a face is a few calls on images ~48 px across, and a frame has a dozen faces: dozens of calls,
microseconds each, back to back. Each call is split across the whole pool, and the CPU is burned *during* the burst
(≈ 0 cores 50 ms later): more threads cost more CPU and are not faster. Whole-frame calls are the opposite: the same
blur on a 4K frame is 6× faster with 22 threads. The burning pool threads take cores from FFmpeg's decoder and the UI
thread, which is how they cause late frames (decode 15.2–16.1 → 18.6–19.1 ms; `WritePixels` p95 3.6–5.7 →
9.0–11.9 ms).

**Change.** `OpenCvThreads.Configure()` sets the pool to 4 threads (or the CPU count, if smaller) at start-up, before
any OpenCV call — `CvInvoke.NumThreads`, process-wide; Emgu's build ignores OpenCV's `OPENCV_FOR_THREADS_NUM`. The
`VISIONREDACT_OPENCV_THREADS` environment variable overrides it for measurements (-1 = OpenCV's default). The
benchmark harness applies the same setting.

**New measurement.** Outputs are identical for every thread count: the hash of every redacted box over all 892 4K60
frames, the analysis detections (1,217 sampled, 3,839 every frame, same box sums), people (73, 103 tracks), and 892
exported frames redacted.

| Workload | 1 thread | **4 threads** | 22 (default) |
|---|---|---|---|
| App: 4K60 playback, preview on (fps shown, late frames, cores) | 60.1–60.4, 0, 4.2–4.4 | **60.2, 0, 5.6–5.9** | 50.3–51.9, 56–58, 14.4–14.6 |
| App: `WritePixels` avg / p95 | 2.4–2.6 / 3.6–5.7 ms | **2.5–2.8 / 3.7–4.9 ms** | 3.5–5.0 / 9.0–11.9 ms |
| Export 4K60, h264_mf (frames/s, app cores without ffmpeg.exe) | 50.8–52.2, 3.9 | **47.8–48.0, 5.3** | 38.7–40.8, 10.5–10.9 |
| Redaction per 4K60 frame: wall / CPU | 9.2 / 17 ms | **10.6–11.2 / 42–43 ms** | 12.5–12.8 / 203–204 ms |
| Analysis, GPU inference: 4K30 every frame / 4K60 at 5 per second | 65.9–71.0 frames/s / 5.2 s | **64.2–66.9 / 5.2–5.3 s** | 63.4–67.4 / 5.2–5.3 s |
| Analysis, CPU inference (no usable GPU): same | 28.9–29.4 / 6.1–6.5 s | **31.4–32.2 / 5.9 s** | 32.4–33.2 / 5.8–5.9 s |
| People (YuNet + ArcFace) on 4K60 | 6.1–6.7 s | **6.1–6.3 s** | 6.1–7.0 s |
| Timeline storyboard build (app) | 6.2–6.5 s | **5.8–6.0 s** | 6.1–6.7 s |

4 threads plays 4K60 with preview redactions at full rate with less than half the CPU, exports 4K about 20 % faster,
and matches the default on every analysis path. 1 thread is slightly better still on this GPU laptop (1.5 fewer cores,
export about 7 % faster) but 8–11 % slower in every-frame analysis when inference runs on the CPU, whose preprocessing
works on whole frames.

**Found along the way.**
- 2 threads was worse than both 1 and 4 for tiny jobs in every measurement (and 48–50 fps with late frames in the app,
  before the restart); the cause is not established.
- A first analysis run in the harness ran inference on the CPU (its detector host defaults to CPU; the app's picker
  chooses the GPU): those numbers were discarded and the harness now selects the GPU like the app.
- The first run after a build or a boot is slower (cold start): a warm-up run is discarded.
- On this hybrid CPU (6 performance, 8 efficiency and 2 low-power efficiency cores) Windows can throttle a background
  console process onto efficiency cores; the harness opts out of power throttling, as the app's foreground window
  normally is.

**Trade-off.** One process-wide setting for every OpenCV caller. A future whole-frame effect (e.g. full-frame blur)
would run slower than with the default pool (4K blur: 15 ms with 4 threads, 5 ms with 22) — measure it and revisit
the setting then.

## Public demo video: both models on Tears of Steel (B9)

To demo and reproduce results without private footage, `scripts/download-demo-video.ps1` fetches **Tears of Steel**
(Blender Foundation, CC BY 3.0 — credit "(CC) Blender Foundation | mango.blender.org"): 1920×800, 24 fps, 17,620
frames, 12:14. `--scenes` (B9) runs both face models over every 6th frame with the app's default thresholds on the RTX
3500 and compares them. There is **no ground truth** here — this compares the models with each other, it does not
measure accuracy against human labels.

| model | frames with ≥ 1 face | faces | small (< 32 px) | medium (32–96 px) | large (≥ 96 px) | ms / frame |
|---|---|---|---|---|---|---|
| YOLO11n-face | 1,578 of 2,937 (53.7 %) | 2,174 | 9 | 258 | 1,907 | 7.7 |
| UltraFace RFB-320 | 1,349 of 2,937 (45.9 %) | 1,878 | 0 | 136 | 1,742 | 4.0 |

Agreement (IoU ≥ 0.4): **1,717 faces found by both**, 457 only by YOLO, 161 only by UltraFace. The film is mostly
close-ups, so both models do well; no 10-second scene had faces only YOLO could find (on the private cricket footage,
with ~30 px faces, UltraFace found none). Scenes where ≥ 80 % of sampled frames agree — good for demos:
**00:30–00:40 (98 %)**, **04:00–04:10 (98 %)**, **04:30–04:40 (100 %)**, 05:10–05:20, 06:30–06:40, 07:30–07:40, 08:30–08:40.

Note: the mirror serves the film as a ZIP named `.mov`; the script extracts it and pins the video's SHA-256.

## Where time goes (measured)

```text
Playback (1080p59.94 AVCHD):   decode 3.8 ms  ─ WritePixels ~1 ms (UI)  ─ budget 16.7 ms/frame   → headroom
Analysis (YOLO, CPU):          decode ~4 ms   ─ preprocess 2.1 ms ─ ONNX ~18 ms ─ NMS < 0.1 ms  → inference-bound
Analysis (YOLO, RTX 3500):     decode ~4 ms   ─ preprocess 2.1 ms ─ ONNX ~2.5 ms                → decode/preprocess-bound
Export (after opt. 7):         decode ‖ blur 1.5 ms/region ‖ FFmpeg h264_mf (≈2.4 ms)           → decode/pipe-bound, 147–167 frames/s
```

## Bounded memory, measured

- Live native frames never exceeded **10** in any run (playback + analysis concurrently), independent of video length.
- The memory soak (B5) shows private bytes varying within **184–238 MB** over 6 full passes and ending at 195 MB: no
  upward trend. The integration test `Long_playback_keeps_native_frames_and_memory_bounded` asserts < 64 MB growth
  after warm-up and that the live-frame counter returns to its baseline.
- The app with a 4.3 GB file open, YOLO loaded and analysis running used ~750 MB working set (diagnostics panel):
  ONNX Runtime arenas and the model (~150 MB), three FFmpeg decoder contexts, and WPF.

## Known limits and candidates (not yet done — no claims)

| Candidate | Why it might help | How to decide |
|---|---|---|
| Hardware video decode (D3D11VA / NVDEC) | with GPU inference the pipeline is decode-bound (queue 0/4) | FFmpeg-based decoder adapter behind `IVideoDecoder`; measure B1/B4 |
| Probe order: NVENC back before `h264_mf`? | `h264_mf` was moved first after NVENC measured 17–20 % slower in the pipeline at 1080p, equal at 4K60 (PC in use) | re-measure on an idle PC, frames/s and CPU, with each encoder |
| Quick Sync before software Media Foundation | hardware encoding that works today (`h264_qsv` or `h264_mf -hw_encoding 1`) and frees ~4–7 CPU cores; slower than the software encoder in isolation on this CPU (optimization 7) | measure a real app export with each encoder (frames/s and CPU) before changing the probe order |
| FFmpeg-based decoder (hardware decode) | with the FFmpeg writer, decode + blur are the remaining export stages | `IVideoDecoder` over FFmpeg; measure B1/B4 and `--export` |
| High-resolution waitable timer for playback pacing | Windows timer granularity (~15.6 ms) adds jitter | measure presentation interval jitter at 60 fps |
| Frame pool for decoded Mats | native allocation per frame | only if an allocation profile shows native heap cost — none observed so far |
| Bgr32 bitmap / D3DImage | WPF converts Bgr24 during composition; a D3DImage presenter would take the copy off the UI thread | measured with the "Present time (WritePixels)" row (app, 4 OpenCV threads): 1080p60 avg 0.58 / p95 0.73 ms, 4K30 1.96 / 2.36 ms, 4K60 2.5–2.8 / 3.7–4.9 ms of a 16.7 ms frame; 0–5 frames replaced per run, so the UI thread keeps up. Revisit for 8K or several videos at once (WPF's render-thread upload is not included in this row) |
