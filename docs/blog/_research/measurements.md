# Measurement log for the blog series

Every number used in a post must appear here with how and where it was measured. The final fact check reads this file.

## Timeline previews — Diagnostics panel (2026-10-10, Tears of Steel 1080p, 1920×800, 24 fps, Release build)

| Metric | Value | How |
|---|---|---|
| Storyboard tiles | 240 / 240 | Diagnostics → Timeline previews |
| Tile memory | 10.55 MB | 240 tiles × 192×80×3 bytes (≈ 46 KB each) |
| Storyboard build time | 7.1 s | Diagnostics, whole 12:14 video, own decoder, coarse-to-fine |
| Preview decoders open | 1 (frame reader) after the build | Diagnostics; 2 while the storyboard builds |
| Frame read time (exact frame) | 37.2 ms average | seek + decode + resize on the frame reader |
| Hover requests | 22 · 1 shown · 21 superseded; later 77 · 2 · 75 and 57 · 2 · 55 | Fast sweeps over the timeline: latest request wins, the rest never decode |
| Presented FPS during playback | 23.9 on a 24.00 fps video | Diagnostics (2 s window) |
| Decode-ahead buffer | 3 / 3 | Diagnostics |

## 60 fps playback in the real app (2026-10-10, Release build, window 1920×1032 on a 1080p monitor)

Read from the Diagnostics panel after 10 s of playback from the start of each clip (2 s rate window).

| Clip | Video fps | Presented FPS | Decode FPS | Avg decode | Buffer | Late drops | UI frames replaced | Live native frames | Working set |
|---|---|---|---|---|---|---|---|---|---|
| crowd-crossing_1080p60.mp4 | 60.00 | 60.1 | 59.6 | 3.5 ms | 3 / 3 | 0 | 1 | 5 | 517 MB |
| crowd-crossing_4k60.mp4 | 60.00 | 60.2 | 60.2 | 14.9 ms | 3 / 3 | 0 | 1 | 5 | 1.06 GB |

The 4K60 decode budget is 16.7 ms per frame, and the average decode time is 14.9 ms (the B10 benchmark measured p99
27.7 ms). The 3-frame buffer absorbs the slow frames, so no frame arrived late. The working set at 4K includes the
decoder's own buffers and WPF's copy of the bitmap. Live native frames stay at 5.

## Smooth playback: frames on a clock, pixels without layout (2026-10-11, the three comparison clips)

Release build, window 1920×1032, Diagnostics open, 10 s of playback from the start. "Layout passes / s" counts WPF
layout passes (LayoutUpdated on the main window). Per frame the video is `WritePixels` into one reused WriteableBitmap,
the playhead is a TranslateTransform and the boxes are `AffectsRender`: all render-only. The only per-frame layout came
from the time and frame text; it now changes at most every 100 ms while playing (exact when paused).

| Clip | Presented FPS | Late drops | Avg decode | Buffer | Layout passes / s, text every frame | Layout passes / s, text ≤ 10/s | Live native frames | Private bytes |
|---|---|---|---|---|---|---|---|---|
| street-flags_1080p30 | 30.0 → 30.1 | 0 | 4.3 → 3.9 ms | 3 / 3 | 30.0 | 9.6 | 5 | 509 MB |
| crowd-crossing_1080p60 | 59.7 → 59.6 | 0 | 3.8 → 3.7 ms | 3 / 3 | 60.1 | 11.2 | 4–5 | 534 MB |
| street-flags_4k30 | 29.7 → 30.3 | 0 | 15.2 ms | 3 / 3 | 31.3 | 9.4 | 5 | 1.02 GB |

With text every frame, layout passes per second equalled the frame rate; after the change they stay near 10 whatever
the frame rate (the 10 Hz time label plus the Diagnostics panel's own 2 Hz rows).

Experiment (2026-10-11): giving both TextBlocks a fixed Width and Height and updating them every frame again still gave
one layout pass per frame: 31.7 / 60.2 / 30.5 layout passes per second (1080p30 / 1080p60 / 4K30), presented 29.7 /
59.7 / 29.7, 0 late. `TextBlock.Text` is registered AffectsMeasure, so every change schedules a measure; a fixed size
only keeps the pass local (the parent is not re-measured). Fewer text changes (≤ 10/s) is what removes the passes.

## Zero layout passes while playing: retained drawings (2026-10-11, the three comparison clips)

Each clip scanned first (YOLO11n, 5 samples/s), then played 10 s with Diagnostics open, so AI boxes move on every frame.
WPF source (dotnet/wpf main): `TextBlock.Text` is `AffectsMeasure | AffectsRender`; an `AffectsRender` change calls
`InvalidateVisual()`, and `UIElement.InvalidateVisual()` calls `InvalidateArrange()` — so a custom OnRender control
also costs an arrange pass per change. A sub-property change of a RenderTransform skips `InvalidateArrange`, and
WritePixels changes only bitmap content: both layout-free.

| Clip | Detections | Boxes `AffectsRender` + text ≤ 10/s | Boxes + text in retained `DrawingGroup`s, text every frame | Presented | Late |
|---|---|---|---|---|---|
| street-flags_1080p30 | 639 | 31.9 layout passes / s | **2.0** | 30.4 → 29.8 | 0 |
| crowd-crossing_1080p60 | 1,208 | 60.1 | **2.0** | 59.7 → 59.4 | 0 |
| street-flags_4k30 | 647 | 31.6 | **2.0** | 30.0 → 29.9 | 0 |

The remaining 2.0 per second is the Diagnostics panel refreshing its own rows (2 Hz). Playback itself — video pixels,
boxes, playhead and timecode, all changing every frame — runs no layout pass.

## The last 2 per second: the Diagnostics panel itself (2026-10-11)

**Evidence.** Visual Studio profiler capture of the d2cec0b build (Report20261011-0131.diagsession), WPF ETW provider
`{E13B77A8-14B6-11DE-8069-001B212B5009}`: 7,087 events decoded (LayoutBegin 7001, MeasureElementBegin 7008,
ArrangeElementBegin 7013, OnRenderBegin 11063, element ids from 10/11). From 27 s to the end: 60 layout passes in
30 s, one every 0.5 s. Each pass measured only Diagnostics rows (a Grid with a label and a value TextBlock): 4 value
TextBlocks when the numbers kept their width, 18 elements (rows, ContentPresenters, the StackPanel) when one got wider.
One more TextBlock outside the panel: the timeline hover card's time label (192 px wide, under the 192×108 thumbnail),
measured 43 times while hovering. The hover thumbnail itself (one reused WriteableBitmap) was measured 3 times.
Also found by reading the code: the decode-ahead `ProgressBar` sets its indicator's `Width` on every change
(dotnet/wpf `ProgressBar.cs`: `_indicator.Width = percent * _track.ActualWidth`).

**Change.** `RenderOnlyText` gained `TextAlignment`, `TextWrapping` and a UI Automation peer. Its width comes from the
parent, never from the text; only a change of height (a value gaining or losing a line) asks for a layout pass. Used
for the Diagnostics values and the hover time. The decode-ahead bar is a full-width rectangle scaled by a
`ScaleTransform` (a sub-property change, like the playhead).

| Scenario (Release build, Diagnostics open) | Before (d2cec0b) | After |
|---|---|---|
| No video, values changing at 2 Hz | 2.0–3.0 layout passes / s | **0.0** |
| 1080p30 / 1080p60 / 4K30 playing, no boxes | 2.0 | **0.0 / 0.0 / 0.0** (30.0 / 59.9 / 29.8 presented, 0 late) |
| Same, scanned (YOLO11n), boxes moving | 2.0 | **0.0 / 0.0 / 0.0** (30.0 / 59.3 / 29.7 presented, 0 late) |
| 1080p60 paused, pointer sweeping the timeline (127 hover requests in 4 s) | 35.7 | **0.0** |

The hover row was measured both ways with the same script, in the same session: rows read 4 s into an 8 s sweep (the
rate is averaged over 2 s). A long value still wraps: "DirectML: NVIDIA RTX 3500 Ada Generation Laptop GPU" takes two
lines, right-aligned, and the rows below move down (that one change is a layout pass, by design).

## Preview redaction cost per frame (2026-10-11, the three comparison clips, PC not idle)

**Harness** (scratch, the app's own classes): AnalysisService scan (YOLO11n face, 5 samples/s, IoU tracker; same
detections as the app: 639 / 1,208 / 647), then every frame decoded and `RedactionPreviewProcessor.Process` timed on
one thread, like the playback producer. Default settings: Gaussian blur, padding 0.2, strength 0.8. Milliseconds.

| Clip | Boxes per frame avg (max) | Redaction avg / p95 / max | Decode avg | Decode + redaction avg / p95 | Budget | Over budget |
|---|---|---|---|---|---|---|
| 1080p30 | 9.1 (15) | 6.6 / 11.5 / 40 (run 1: 9.4 / 26.2 / 119) | 4.5 | 11.1 / 20.2 | 33.4 | 1.4% |
| 1080p60 | 16.1 (23) | 5.7 / 9.6 / 15 | 4.3 | 10.1 / 13.7 | 16.7 | 0.8% |
| 4K30, runs 1–2 | 9.2 (15) | 14.4 / 26.5 / 66 | 17.3 | 31.6 / 43.9 | 33.4 | 26% |
| 4K30, run 3 | 9.2 (15) | 7.9 / 12.5 / 50 | 14.1 | 22.0 / 27.6 | 33.4 | 0.7% |

The 4K runs differ about 2× in redaction and ~20% in decode: machine state (the PC was in use; a stray `find` held one
core). Per 128-frame segment in run 3: 10.8 / 5.4 / 8.3 / 4.9 ms average, following the number of boxes
(11.6 / 6.8 / 10.2 / 6.2). Redo on an idle PC before publishing.

**Real app** (Release, Diagnostics open, 11 s from the start, "Preview redactions" on / off): presented 29.9 / 30.0,
58.8–60.1 / 59.9, 29.9 / 30.0 fps; late frames dropped 0 everywhere; preview frames replaced 0–9 with preview on or
off alike (the UI Automation polling runs on the UI thread). The decode-ahead buffer read 3/3 in every sample: the
producer holds a finished frame while the channel is full, so the channel refills instantly and reads 3/3 whenever the
producer keeps up on average — a yes/no signal, not a margin. New row (commit 296cf8a, last 128 frames, read at ~10 s):
1080p30 avg 4.6 / p95 11.6 / max 14.3; 1080p60 4.6 / 5.8 / 6.8; 4K30 6.4 / 10.7 / 12.3; 0.0 with preview off. The 4K
reading falls between the harness segments covering the same frames (5.4 and 8.3 average).

## 4K60 stress test: where preview playback breaks (2026-10-11, crowd-crossing_4k60, PC not idle)

Added at the user's request to see whether dropping frames keeps playback smooth. Same scene as crowd-crossing_1080p60
(1,217 detections, 16.2 boxes per frame). Harness: real `PlaybackSession`, real decoder and processor, a sink that
timestamps each presented frame (no UI thread). Frame budget 16.7 ms.

| | Decode avg | Redaction avg / p95 | Shown | Late drops | Gaps between shown frames | Wall time for 14.85 s | Lateness p50 / max |
|---|---|---|---|---|---|---|---|
| 4K60, preview off | 14.0–15.6 ms | – | 891 of 891 (59.9 fps) | 0 | all 1 | 14.88 s (100%) | 6.6 / 15.7 ms |
| 4K60, preview on | 15.3 ms | 18.9 / 23.2 ms (row); 13.6 / 18.7 (timing pass) | 190 of 891 (7.1 fps) | 701 | 175× a gap of 5 (1 shown, 4 dropped), 13× 1, 1× 2 | 26.72 s (180%) | 4.5 s / 11.9 s |

Real app, 11 s from the start: preview on 7.2 fps presented, +364 late drops, buffer 0/3 in all 20 samples; preview
off 58.6 fps, 0 late drops, buffer 3/3 in 15 samples and 2/3 in 5 (at the edge).

**Why dropping does not help here.** Frames are dropped by the consumer *after* the producer decoded and redacted
them, so a drop saves no work. Decode + redaction ≈ 29 ms per frame against 16.7 ms: the producer makes ~34 frames/s.
`MaxConsecutiveLateDrops = 4` then forces one late frame on screen after every 4 drops, and `PlaybackClock` is never
re-anchored, so lateness grows without bound: 1 frame in 5 shown (~7 fps) *and* 1.8× slower than real time. Dropping
works for short spikes (every 1080p and 4K30 run: 0 drops even with individual frames over budget); it cannot fix a
producer that is slower than the frame rate on average.

**Smaller finding, 60 fps without drops.** Time between presented frames at 60 fps: sd 3.9–4.7 ms, p99 31.5–31.8 ms;
59–76 of 890 intervals (7–8%) longer than 1.5 frames; 37–43% of frames presented more than half a frame (8.3 ms) late.
At 30 fps: sd 5.4 ms, no interval over 1.5 frames. Consistent with the documented Windows timer granularity (~15.6 ms)
of the timed wait. On screen, WPF composes at the display's vsync, so a frame that misses its vsync is probably shown
one vsync late or replaced by the next one; not measured on screen.

**Fix (same day): skip before the work.** The decode task predicts whether a frame would be more than one frame late
(now + smoothed full-frame cost vs its due time, from the consumer's published clock anchor) and calls
`IVideoDecoder.Skip()` (Grab: 3.7–4.1 ms at 4K vs 10.4–10.6 ms for Read with a reused buffer) instead of decoding and
redacting it. The cap of 4 counts every frame left out since the one on screen; after it, one is shown and the clock
restarts from it. Step +1 / resume check they got exactly the next frame.

| 4K60, preview on | Before | After (harness) | After (real app, 11 s) |
|---|---|---|---|
| Shown | 190 of 891 (7.1 fps) | 501 of 891 (33.5 fps) | 32.5 fps presented |
| Late drops | 701 (all after decode + redaction) | 390 | +264, of which 247 skipped early (94%) |
| Wall time for 14.85 s | 26.72 s (180%) | 14.97 s (101%) | ≈ real time (~325 shown + 264 dropped ≈ 600 frames in ~10 s) |
| Lateness of shown frames p50 / p99 / max | 4.5 s / 11.8 s / 11.9 s | 9.0 / 139.7 / 143.9 ms | – |
| Gaps (1 = next frame) | 175× 5, 13× 1, 1× 2 | 206× 1, 249× 2, 11× 3, 17× 4, 17× 5 | – |
| Time between shown frames avg / sd / p99 | 141 / 43 / 248 ms | 29.9 / 14.8 / 95.4 ms | – |

Unchanged elsewhere (after the fix): 1080p30, 1080p60, 4K30 with and without preview, and 4K60 without preview, show
every frame in real time (1080p60 preview on: 1 drop in 891). Real app: 28.5–30.0 / 59.4–60.3 / 30.0 / 59.1 fps,
0 late drops. Tests: 165 pass (3 new: real time with a slow decoder, step and resume land exactly on the next frame).
Remaining unevenness at 4K60: 45 gaps of 3–5 frames and a p99 lateness of 140 ms, where redaction cost peaks (up to 33
ms per frame); the next lever is less work per shown frame (decode and redaction on two threads).

## Tears of Steel at 4:39 (theme screenshot, 2026-10-10)

Presented 24.1 fps on a 24.00 fps video, 3 / 3 buffer, 0 late drops. The storyboard has 240 tiles (10.55 MB), built
in 9.2 s.

## Tile lookup: microbenchmark vs the real app (2026-10-10)

`Storyboard.Nearest` over 240 tiles (binary search). Bench: `scratchpad/lookupbench` (Release, TimeProvider.System, 10 MHz timestamps).

| Condition | Median | p90 |
|---|---|---|
| Tight loop, hot caches (1,000,000 calls) | 70.8–73.4 ns per lookup | — |
| Tight loop, timed individually | 100 ns (timer resolution) | 100–200 ns |
| One lookup every 10 ms (CPU idles in between) | 1,800 ns | 2,500 ns |
| One lookup every 10 ms + 64 MB cache pollution in between | 2,300 ns | 7,300 ns |
| Very first call (JIT compile) | 96,600–109,000 ns | — |
| In the app (Diagnostics, median of last 64) | 5,000 ns | — |

Lesson: a microbenchmark measures hot caches; the UI thread pays the cold-cache cost. Both are true. A mean was
first used in the app and read 7,573–11,818 ns because one JIT-cold first call dominated it, so the panel now shows
the median of the last 64 lookups. 5 µs = 0.03 % of a 16.7 ms frame at 60 Hz.

## Decode and redaction on two tasks (2026-10-11, 4K only, real app, A/B against fe35c6d)

The preview processor moved from the decode task to its own redaction task, joined by a one-frame channel, so
decoding the next frame overlaps redacting this one. No pixels are copied: the same frame object is passed on. Both
builds run back to back on the same machine (PC in use), each with the same models, scanning then playing 11 s with
preview redactions on. CPU = process CPU time / wall time (cores busy on average).

| Clip | Build | Presented | Late (+) | CPU cores | Peak private | Decode / redaction avg |
|---|---|---|---|---|---|---|
| 4K30 | before | 29.5 fps | 0 | 5.47 | 1,508 MB | 14.7 / 7.9 ms |
| 4K30 | after | 30.0 fps | 0 | 4.88 | 1,641 MB | 17.5 / 8.9 ms |
| 4K60 | before, run 1 / 2 | 26.9 / 31.2 fps | 264 / 252 | 9.87 / 9.91 | 1,511 / 1,524 MB | 16.1 / 15.6, 15.9 / 14.9 ms |
| 4K60 | after, run 1 / 2 | 47.2 / 51.9 fps | 87 / 65 | 14.20 / 13.72 | 1,551 / 1,604 MB | 19.3 / 16.7, 18.5 / 17.1 ms |

4K60: about 1.7× more frames shown (≈29 → ≈50 fps). CPU +40% in total, but per shown frame it fell (≈0.34 → ≈0.28
core-seconds), because more of the decoded frames are now shown instead of skipped. Each stage got 15–20% slower:
FFmpeg's decoder and OpenCV's blur are both multi-threaded and now run at the same time (4K30 preview off: 1.53
cores; on: 3.79–5.47 — the blur alone keeps ~2–4 cores busy at 30 fps). Memory: +2 frames by design (≈ 50 MB at 4K);
peak live native frames 5 → 7. 4K30 already kept up before: no visible change there.

## Where the redaction CPU goes, and blurring on the GPU (2026-10-11, scratch probe, real 4K frames and face boxes)

The app's scan (YOLO11n face, 5 samples/s), then for 240 frames per clip the same redaction plan applied to three
copies of each frame: the app's `RedactionProcessor` (OpenCV default: 22 threads), the same with
`CvInvoke.NumThreads = 1`, and the same steps on OpenCL `UMat`s (Emgu CV's built-in GPU path; device: Intel Arc Pro
iGPU), including copying each box to the GPU and back. CPU time = process CPU time during the call.

| Clip | Boxes per frame | Variant | Wall avg / p95 | CPU time per frame |
|---|---|---|---|---|
| 4K60 crowd | 11.6 (max 16) | OpenCV default (22 threads) | 9.26 / 13.18 ms | 120.0 ms (13.0 cores busy) |
| | | OpenCV 1 thread | **6.40 / 9.23 ms** | **10.9 ms** |
| | | OpenCL (GPU) | 32.07 / 21.95 ms (max 819: kernel builds) | 34.2 ms |
| 4K30 street | 9.3 (max 14) | OpenCV default | 8.25 / 13.12 ms | 82.7 ms (10.0 cores) |
| | | OpenCV 1 thread | **5.60 / 8.52 ms** | **18.9 ms** |
| | | OpenCL (GPU) | 13.64 / 15.56 ms | 15.9 ms |

- Most of the redaction's CPU is OpenCV's thread pool, not blurring: dozens of small calls per frame (resize, blur,
  resize per box) each wake the pool. One thread is faster in wall time and uses ~5–11× less CPU. This explains most
  of the ~14 cores the app used at 4K60 with preview on.
- OpenCL works out of the box in Emgu CV, but per-box uploads, downloads and kernel launches make it slower than the
  CPU for these small regions, and its pixels differ from the CPU's at the box edges (max 134 / 90 of 255; ≤ 0.18 %
  of a frame's values more than 1 level apart) — the CPU blur of a sub-image reads the pixels around the box, the
  isolated GPU copy cannot. Preview would no longer match export exactly.
- `CvInvoke.NumThreads` is process-wide in OpenCV: changing it also affects AI preprocessing, export and tracking,
  so measure those before changing it.

## OpenCV thread-pool size: 1, 2, 4, 8, default (2026-10-11, PC in use)

Set with `CvInvoke.NumThreads` (process-wide; Emgu's build ignores OPENCV_FOR_THREADS_NUM), one size per process.
Harness = the app's own classes (scratch `cvbench`); app = Release build, 4K60 crowd clip scanned then played 11 s
with preview redactions on, two rounds in different orders.

**Outputs identical for every size:** redacted-box hash `0D59AE15F17F9396`; analysis 1,217 detections (5/s) and
3,839 (every frame) with identical box sums; people 73 / 103 tracks; export 892 frames redacted. Decode (FFmpeg's own
threads) unaffected: 12.7–13.6 ms per 4K frame.

| Threads | Analysis 4K60 5/s | Analysis 4K30 every frame | Redact 4K60 wall / CPU per frame | People 4K60 | Export 4K60 (h264_mf) | App 4K60 playback: shown fps, late, CPU (2 rounds) |
|---|---|---|---|---|---|---|
| 1 | 6.31 s | 29.6/s | 9.7 / 18.7 ms | 6.13 s | 50.3 fps, 3.8 cores | 60.1 / 59.6, 0 / 0, 4.8 / 4.5 |
| 2 | 6.01 s | 29.6/s | 12.9 / 31.9 ms | 6.04 s | 46.9 fps, 4.2 cores | 49.7 / 48.2, 47 / 36, 4.7 / 4.9 |
| 4 | 5.88 s | 31.9/s | 11.2 / 45.1 ms | 5.99 s | 48.0 fps, 5.2 cores | 59.4 / 59.9, 0 / 0, 6.2 / 6.0 |
| 8 | 5.95 s | 32.4/s | 9.7 / 67.0 ms | 6.00 s | 45.5 fps, 6.9 cores | 60.0 / 60.5, 0 / 0, 7.6 / 7.8 |
| default (22) | 5.84 s* | 32.9/s* | 12.6 / 200.8 ms | 6.80 s | 38.3 fps, 10.3 cores | 48.5 / 50.1, 62 / 63, 14.1 / 13.3 |

\* Repeat run: the first default run was a cold start (18.8 s / 7.2/s) and is not used. Harness columns are from one
batch (the repeat batch ran ~15% slower overall: 1 thread 7.50 s / 26.3/s, default 5.84 s / 32.9/s).
Storyboard (timeline thumbnails) build time in the app: 5.8–6.4 s for every size — no effect.

**WritePixels (Present time row, UI thread):** 1080p60 avg 0.58 / p95 0.73 ms (3–4 % of a 16.7 ms frame); 4K30
1.96 / 2.36 ms; 4K60 2.3–3.1 / 3.1–7.6 ms with 1–8 threads, 3.4–4.9 / 9.6–11.6 ms with the default 22 (the UI
thread competes for cores). Preview frames replaced: 0–4 per run — the UI thread keeps up.

## Why the OpenCV thread count matters — proof on a freshly restarted, idle PC (2026-10-11, booted 05:03)

Visual Studio, ReSharper, codex and ChatGPT closed; CPU 3–5 % before starting; AC power, High performance plan.
Emgu's OpenCV build: `Parallel framework: Concurrency` (Microsoft ConcRT, CONCRT140.dll).

**Mechanism** (scratch `mechanism.cs`, one process per size, two rounds; "face" = exactly the GaussianBlurEffect steps
on a 300×300 box: area-shrink to 48×48, two 31×31 Gaussian passes, linear enlarge):

| Threads | 12 faces (one frame): wall / CPU | CPU in a 50 ms idle gap after the burst | Whole 4K frame blur 5×5: wall / CPU | 4K → 640×360 resize: wall |
|---|---|---|---|---|
| 1 | 7.0–7.5 / 7.1–7.5 ms (1.0 core) | 0.02–0.05 cores | 32–34 / 32–34 ms | 0.44 ms |
| 2 | 9.7–10.5 / 14–15 ms | 0.00–0.01 | 33–35 / 50–52 ms | 0.35–0.38 ms |
| 4 | 8.4–8.5 / 22–24 ms | 0.00–0.02 | 15–16 / 30–46 ms | 0.23–0.25 ms |
| 8 | 8.8–9.3 / 46–49 ms | 0.00–0.01 | 9.0–9.4 / 41–46 ms | 0.16–0.19 ms |
| 22 | 11.5–12.3 / 147–162 ms (13 cores) | 0.19–0.30 | 5.3 / 49–56 ms | 0.19–0.21 ms |

Tiny jobs: more threads = more CPU and no speed-up (22 threads: slowest, 21× the CPU). Big jobs: threads pay off
(whole-frame blur 6× faster with 22). The CPU is burned *during* each burst of back-to-back tiny calls, not in idle
gaps (≈ 0 cores 50 ms later). 2 threads is slower than both 1 and 4 for tiny jobs (consistent; cause not proven).

**Where the CPU goes in the app** (`measure_threads.ps1`: 4K60, scanned, playing with preview, every thread's CPU
over 6 s, attributed by its Win32 start address via NtQueryInformationThread):

| Threads | App total | CONCRT140.dll threads (OpenCV pool) | msvcrt.dll threads (FFmpeg decode) | coreclr (.NET tasks, GC) | wpfgfx (WPF render) + UI thread |
|---|---|---|---|---|---|
| default 22 | 15.4 cores | 22 threads, all busy, **9.83 cores** | 16, 2.62 | 1.87 | 0.71 + 0.33 |
| 4 | 6.38 | 4 threads (3 busy), **1.50** | 16, 2.33 | 1.61 | 0.46 + 0.44 |
| 1 | 4.29 | **none** (OpenCV runs on the calling thread) | 16, 1.91 | 1.49 | 0.56 + 0.25 |

**Clean-machine comparison (same session, after the restart)** — 1 / 4 / 22 threads, app 4K60 playback in mirrored
order, harness with GPU inference like the app plus a CPU-inference check: full tables in docs/Performance.md,
optimization 8. Decision: 4 threads (`OpenCvThreads.Default`), the only setting at least as good as the default on
every workload (60.2 fps vs 50–52, 0 vs 56–58 late, 5.6–5.9 vs 14.4–14.6 cores; export 48 vs 39–41 frames/s; analysis
equal on GPU and CPU inference). Redacted-box hash in this session `2AC4687B35CCE48C` for every size (the earlier
`0D59AE15F17F9396` came from CPU-inference detections, which differ slightly in box coordinates).

## Modest machines, simulated (2026-10-11 night, same idle session)

Simulated by restricting the process (and the ffmpeg.exe it starts) to some of this CPU's logical processors from
launch: **mid-range** = 4 performance cores with hyper-threading (8 threads, mask FC03); **low-end** = 4 efficiency
cores (mask 3C). This CPU's cores are fast, so a real laptop of that class may be somewhat slower. Release build,
app default settings; AI on the GPU unless stated. App rows: 11 s from the start, preview redactions on.

| Workload | Full machine (22 threads) | Mid-range (4C/8T) | Low-end (4 E-cores) |
|---|---|---|---|
| App 1080p30 | 30 fps, 0 late | 30.0 fps, 0 late, 0.9 cores | **30.0 fps, 0 late**, 1.0 cores |
| App 1080p60 | 60 fps, 0 late | **59.9 fps, 0 late**, 1.2 cores | **18.4 fps**, 270 late (19 skipped early) |
| App 4K30 | 30 fps, 0 late | **30.0 fps, 0 late**, 2.6 cores | 8.2 fps, 154 late (decode 59.5 ms > 33.4 ms budget) |
| App 4K60 (stress) | 60 fps, 0 late | 31.8 fps, 157 late (94 skipped early) | 5.0 fps, 174 late |
| Decode per frame, 1080p / 4K (harness) | ~3.7 / ~13–16 ms | 3.2 / 12.8 ms | 12.6 / 41.1 ms |
| Export 1080p60 / 4K60 (h264_mf) | – / 47.8–48.0 fps | 121.1 / 31.0 fps | 53.1 / 15.8 fps |
| AI scan 5/s, 1080p60 clip (14.9 s): GPU / CPU inference | – | 2.9 s / 15.2 s | 5.2 s / 64.3 s |
| AI every frame, 1080p30: GPU / CPU inference | – | 145 / 5.7 fps | 70 / 1.3 fps |
| People, 1080p60 clip | – | 9.0 s | 36.4 s |

Low-end 1080p60, what limits it (all 1080p60, preview on unless stated): OpenCV threads 1 / 2 / 4 → 18.5 / 23.0 /
17.1 fps (not the thread count); **preview redactions off → 52.8 fps** (90 late). Redaction on these cores: avg
8–12 ms, p95 24–34 ms per frame; the app uses only ~1.3 of the 4 cores, so the limit looks like per-frame latency and
hand-offs between threads on slow cores, not throughput. Not yet profiled — a candidate for the next round.
Outputs identical across profiles (same detection counts and redacted-box hashes per resolution).

## NVENC after the driver update (2026-10-11 morning, driver 610.88, PC in use; the probe order of that build)

The NVIDIA RTX Enterprise driver 610.88 (`610.88-quadro-rtx-desktop-notebook-win10-win11-64bit-international-dch-whql.exe`)
replaced 596.41; no reboot. The app's FFmpeg 9.0.2 now opens `h264_nvenc`, and because NVENC is first in the probe
order, **every export now uses NVENC automatically**. The software encoder (`h264_mf`) was forced for comparison by
running the process with `CUDA_VISIBLE_DEVICES=-1` (FFmpeg's NVENC needs a CUDA context; DirectML is unaffected). Same
build, same clips, mirrored order per clip (NVENC, MF, MF, NVENC). Harness: `VisionRedact.Benchmarks --export <clip>`,
i.e. `VideoExportService` + `FfmpegVideoWriterFactory`, one blurred region on every frame (not the YOLO boxes of B10).

| Clip | NVENC (p4) frames/s | h264_mf (software) frames/s | NVENC vs software |
|---|---|---|---|
| street-flags_1080p30 (420 frames) | 160.9 / 158.4 | 188.4 / 195.8 | ≈ 17 % slower |
| crowd-crossing_1080p60 (892) | 159.5 / 157.7 | 196.2 / 201.2 | ≈ 20 % slower |
| street-flags_4k30 (420) | 48.4 / 48.3 | 52.0 / 50.9 | ≈ 6 % slower |
| crowd-crossing_4k60 (892, stress) | 56.2 / 49.7 | 53.8 / 43.0 | within noise |
| 1080p60 on 4 efficiency cores (mask 3C) | 44.1 / 57.3 | 44.6 / 52.8 | within noise |

Standalone FFmpeg, 4K60 test pattern (900 frames, nv12): NVENC 103.9 / 92.7 vs h264_mf 159.1 / 158.8 frames/s.
File sizes equal within 10 % (same target bitrate). CPU cores freed by NVENC inside the pipeline: not measured here.
Conclusion: on this laptop the encoder is not the export bottleneck; NVENC does not make export faster, and at 1080p
it is slower. Why it is slower at 1080p (per-frame upload, synchronous encode, preset) is not profiled.

**What changed afterwards (same day):** because of this result the probe order was changed. Media Foundation's `h264_mf` is now tried first and NVENC second (the `Candidates` list in `FfmpegVideoWriter.cs`). "NVENC is first in the probe order" above describes the build measured here, not the current one.

## Layout time on the UI thread, before vs after the zero-layout change (2026-10-11, restarted idle PC)

**Metric (new Diagnostics row "Layout time (UI thread)").** WPF runs layout at the start of its render tick, a
Render-priority Dispatcher operation (dotnet/wpf `MediaContext.RenderMessageHandlerCore`: animation tick, layout through
the InvokeOnRender callbacks, `CompositionTarget.Rendering`, then `Render()` and the commit to the composition thread),
and raises `LayoutUpdated` when the pass ends. Each pass is timed from the start of that operation (Dispatcher hooks) to
`LayoutUpdated`: a slight upper bound, as it includes the animation tick (no animations run here). Reported per second
over the same 2 s window as the pass count, and per pass.

**Builds.** Before = commit 63fc18c (face boxes redrawn with `InvalidateVisual`, time text ≤ 10/s) with the same row
patched in, built from a temporary worktree; after = the current tree. Same models and FFmpeg files in both outputs.

**Run.** Each clip scanned (YOLO11n, GPU), 8 s settle, Diagnostics open, played from the start; read ~9.5 s in.
Mirrored order: 1080p60 after, 1080p60 before, 4K60 before, 4K60 after.

| Clip | Build | Presented FPS | Layout passes / s | Layout time (UI thread) |
|---|---|---|---|---|
| crowd-crossing_1080p60, boxes moving | before | 60.2 | 60.2 | **62.10 ms/s** (≈ 1.0 ms per pass) |
| crowd-crossing_1080p60, boxes moving | after | 59.7 | 0.0 | **0.00 ms/s** |
| crowd-crossing_4k60 | before | 7.5 (pre-skip build: slow motion, 289 late) | 9.5 | 12.86 ms/s (≈ 1.4 ms per pass) |
| crowd-crossing_4k60 | after | 60.4 | 0.0 | **0.00 ms/s** |

At 1080p60 the old build spent about 1 ms of every 16.7 ms frame (≈ 6 % of the UI thread) in layout; it still held 60
FPS on this laptop, so the gain is headroom, not frames here. The 4K60 "before" ran at 7.5 FPS (that build predates
skipping before the work), so its layout cost at 60 FPS was not measured. The "per pass" figure first shipped as an
average since the panel opened, which the panel's own first layout (~17.7 ms) dominated when no other pass happened;
it now uses the same 2 s window.
