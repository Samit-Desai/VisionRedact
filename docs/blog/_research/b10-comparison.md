# B10: 1080p vs 4K, 30 vs 60 fps: what the numbers say

> **Superseded for numbers (2026-10-11).** B10 was re-run on 2026-10-11 06:33 on a quieter machine (2.6 logical processors busy in the background) with the current
> build (four comparison clips, 3 passes): see `b10-output.txt` ("B10 summary") and `b10-results.json`. The episodes
> quote that run. Detection counts are identical; times, export speeds and ffmpeg.exe memory differ (export is
> 1.7–2× faster than below). The run below is kept as `b10-output-2026-10-10.txt` / `b10-results-2026-10-10.json`.

A summary for the blog author of the B10 benchmark run of 2026-10-10 23:52 (45.7 min). Every number here is copied
from `b10-output.txt` in this folder; the per-stage lines and the JSON (`b10-results.json`) hold the detail. Where a
cell shows `median [min .. max]`, the stage ran in all 3 measured passes and the value is the median, with the lowest
and highest pass in brackets. A single value is either the median of 3 passes (the full range is in
`b10-output.txt`) or, where marked "pass 1", a single run.
Ratios such as "x4.1" are computed from the two values printed next to them.

## The videos

| short name | file | resolution | fps | frames used | file bitrate | faces in shot |
|---|---|---|---|---|---|---|
| street 1080p30 | street-flags_1080p30.mp4 | 1920x1080 | 29.97 | 420 (all) | 11.1 Mbit/s | busy street |
| street 4K30 | street-flags_4k30.mp4 | 3840x2160 | 29.97 | 420 (all) | 32.4 Mbit/s | same clip, 4K |
| crowd 1080p60 | crowd-crossing_1080p60.mp4 | 1920x1080 | 60 | 892 (all) | 25.7 Mbit/s | dense crowd |
| crowd 4K60 | crowd-crossing_4k60.mp4 | 3840x2160 | 60 | 892 (all) | 71.0 Mbit/s | same clip, 4K |
| close-up 4K30 | group-closeup_4k30.mp4 | 3840x2160 | 29.97 | 358 (all) | 17.3 Mbit/s | three large faces |
| Tears 1080p24 | tears_of_steel_1080p.mov | 1920x800 | 24 | first 1,800 of 17,620 | 6.4 Mbit/s | film, few faces |

One decoded frame (3 bytes per pixel, BGR) is 5.9 MiB at 1080p and 23.7 MiB at 4K: four times the pixels
(62 vs 249 Mpx/s at 30 fps; 124 vs 498 Mpx/s at 60 fps). Each resolution pair is the same clip, so only the pixel
count changes. The 30 fps and 60 fps clips are **different clips** (different content, bitrate and number of faces),
so "30 vs 60" comparisons below are about the cost per frame times the frames needed per second.

## Key findings

**1. Work on every pixel costs several times more at 4K (x2.5 to x5); the AI models, the detections and the memory bounds do not.**
Decoding a frame with `ReadNext` (decompress + convert to BGR + a new frame buffer) went from 3.86 to 15.77 ms on the
street clip (x4.1) and from 4.09 to 13.43 ms on the crowd clip (x3.3). Export per frame went from 10.0 to 49.8 ms
(street, x5.0) and 6.9 to 30.7 ms (crowd, x4.4); the output file from 11.4 to 43.2 MiB and from 24.0 to 92.4 MiB; a
random seek from 243 to 836 ms (x3.4) and from 246 to 604 ms (x2.5). What did *not* grow: the models resize every frame to a fixed input, so
YOLO26l's Detect time stayed at 75.7 vs 75.6 ms, the faces found per frame were the same (YOLO11n 9.1 vs 9.1 on the
street clip, 16.2 vs 16.2 in the crowd), and GPU memory per model was identical (86, 157 and 1075 MiB). The small
models' Detect time did grow (UltraFace 4.7 to 11.5 ms, YOLO11n 6.8 to 11.1 ms on the street clip), because Detect
includes shrinking the full frame to the model input. In short, 4K costs several times more to read, seek and write,
and buys these models nothing in detections.

**2. 60 fps playback keeps real time, but at 4K60 the margin is thin and the decode-ahead buffer is what saves it.**
*Headroom* is how much faster than real time one decode thread runs (decode fps / video fps). It is 8.03x for street
1080p30, 3.77x for crowd 1080p60, 1.98x for street 4K30 and only 1.16x [1.14 .. 1.18] for crowd 4K60, whose frames
take 13.43 ms on average against a 16.7 ms budget (p99 17.3 ms). With the app's 3-frame *decode-ahead buffer*
(frames decoded early and queued) 4K60 played with 0 late drops in two of three passes. The third pass dropped 483
frames and stalled once (presentation up to 1,119 ms late) while other processes used 9.5 logical processors. With a
1-frame buffer, 4K60 dropped 129 of 892 frames (51.2 fps presented) even in pass 1. All other clips: 0 drops with
either buffer. Separately, *presentation lateness* (when the benchmark's sink received a frame minus when it was
due) has a p99 between 14.3 and 15.4 ms on every clip and in every pass except the stalled 4K60 pass, at 1080p and
4K alike. At 60 fps that means a median of 337 to 351 of 892 frames arrive more than half a frame (half of 16.7 ms)
late; at 30 fps, at most 4. Because it does not depend on resolution, this looks like the granularity of the Windows
timer that the playback loop's waits rely on (PlaybackSession.cs notes this limit), not decoding. That is an
inference; the harness does not measure the timer. These numbers end at the sink: the WPF bitmap upload and screen
refresh are not included.

**3. At 4K the slow part of reading a frame is not decompression.** The harness splits `ReadNext` three ways:
*grab only* (decompress, no picture), *Read into one reused buffer* (decompress + colour conversion), and `ReadNext`
(also allocates a fresh frame buffer). Street 4K30: grab 6.98 ms, colour conversion 7.04 ms, new buffer 2.77 ms.
Crowd 4K60: grab 3.84 ms, conversion 7.03 ms, new buffer 2.59 ms. (These are medians of per-pass differences, so they
do not add up exactly.) The conversion runs on the calling thread: the `ReadNext` loop keeps 3.1 to 3.2 logical
processors busy at 4K, against 6.7 to 9.5 for the grab-only loop, where the decoder's own threads are working. A
reused buffer alone would take 4K60 from 13.43 to 10.88 ms per frame; skipping the conversion (as `Skip()` does for
frames the AI scan does not analyze) leaves 3.84 ms.

**4. The bottleneck moves with resolution.**
- *Playback*: always the single decode thread (finding 2).
- *AI scan, small models*: at 1080p the detector is the limit; its input queue is full (4/4) and 6 frames are alive,
  the design maximum. At 4K the decoder is the limit: YOLO11n's queue never holds more than 1 frame and it analyzes
  48.9 frames/s on street 4K30 (decode alone: 59.3) and 64.3 on crowd 4K60 (decode alone: 69.8).
- *AI scan, YOLO26l*: always the GPU, at 12.0 to 13.5 analyzed frames/s on every clip, with the busiest GPU engine 61
  to 73 % busy.
- *Export*: at 1080p the export holds the design maximum of 11 frames (both 4-frame queues full), which points at
  the last stage, the encoder. At 4K the peak is 6 (street), 7 (close-up) and 9 (crowd), and on street 4K30 the app
  and the encoder together use only 6.2 logical processors. That points at a single-threaded stage before the encoder,
  most likely the blur of 9.1 faces per frame on 4K frames. The harness does not time the redaction stage on its own,
  so this last step is an inference.

**5. Memory: frames stay bounded by design; the large consumers are the decoder, the big model and the encoder.**
The exact peak of live frames never exceeded the design bound in any stage of any pass, and reached it in several:
playback 5 of 5, AI 6 of 6, export 11 of 11. The *increase* of a stage is peak private bytes minus the settled level
before it, i.e. the stage's own cost. At 4K, decoding alone adds 476 to 488 MiB, and 404 to 410 MiB when no frame is
kept at all (grab only, 0 live frames). At 1080p these are 48 to 143 MiB and 28 to 121 MiB. With no app frame
alive, that memory must belong to the decoder itself (FFmpeg inside OpenCV, with its worker threads' frame buffers). YOLO26l adds 1,375 to 1,929 MiB plus 1,075 MiB of GPU memory. The export's
`ffmpeg.exe` child process peaks at 2,214 to 2,378 MiB at 4K (725 to 1,098 MiB at 1080p), more than the app's own
export increase (544 to 632 MiB at 4K). There is no sign of a leak: run five times in one process, each model's
settled level moves up and down (UltraFace 502 / 534 / 426 / 501 / 534 MiB, YOLO11n 596 / 625 / 601 / 650 / 638,
YOLO26l 642 / 645 / 646 / 571 / 645) instead of climbing. Across passes 2 and 3 the process sat between 286 and 382 MiB
at the start and end of every video. GPU memory was back to 0 at the start of every AI stage.

**6. Crowds make the models disagree; the close-up does not.** On the close-up, all three models find 3.0 faces per
frame. YOLO11n keeps exactly 3 tracks (one identity per person for the whole clip), UltraFace 4 and YOLO26l 15. In the
4K crowd, UltraFace finds 8.4 faces per frame, YOLO11n 16.2 and YOLO26l 26.3, with 324, 420 and 763 tracks; on street
4K30, 4.2, 9.1 and 26.0 faces with 142, 292 and 828 tracks. Many more tracks than faces per frame means many short
tracks: people entering and leaving, occlusions, and detections the tracker could not link to an earlier one. There
is no ground truth here, so these numbers say how much each model finds, not how much of it is correct. At the app's
default sampling (5 analyzed frames per second) YOLO11n finds the same faces per frame (9.1 to 9.2 on the street
clip, 16.1 to 16.2 in the crowd) with fewer tracks (124 instead of 287 on street 1080p30, 117 instead of 444 on
crowd 1080p60).

**7. Export: faster than real time at 1080p, slower at 4K, and the encoder runs on the CPU.** Steady export speed
(frames handed to the encoder between 10 % and 90 % of the export) was 99.8 frames/s for street 1080p30 (3.33x real
time), 144.1 for crowd 1080p60 (2.40x) and 250.3 for Tears (10.43x). At 4K: 20.1 for street (0.67x real time), 41.7
for the close-up (1.39x) and 32.5 for the crowd at 60 fps (0.54x). The encoder is FFmpeg `h264_mf`, which as called
loads Microsoft's **software** "H264 Encoder MFT" (CPU cores, no hardware flag). NVENC, the NVIDIA hardware encoder,
fails its probe because this FFmpeg build needs NVENC API 13.1 and the driver provides 13.0 ("minimum required Nvidia
driver for nvenc is 610.00"). Counting both processes, an export keeps 6.2 to 13.8 logical processors busy (app 3.3 to
8.7, encoder 2.8 to 5.5). File size follows pixels per second: 6.8 Mbit/s (1080p30), 13.5 (1080p60), 25.9 (4K30) and
52.1 Mbit/s (4K60).

**8. What the app does by default, and what drives seek time.** The app scans with YOLO11n at 5 analyzed frames per
second. That ran at 13.23x real time on street 1080p30 and 11.51x on crowd 1080p60, but 4.00x on street 4K30 and
3.30x on crowd 4K60: every frame still has to be decompressed, and at 4K that dominates. Seeking to an exact frame
starts at the previous *keyframe* (a frame stored whole; the frames after it store only changes), so seek time
follows the *GOP*, the distance between keyframes, as much as resolution. The 4K close-up (GOP 90 frames) seeks in a
median 261 ms, about the same as the 1080p clips with a GOP of 250 (243 and 246 ms). Tears (GOP 18) takes 25 ms. The 4K
clips with a GOP of 250 take 604 to 836 ms.

## Tables

Columns: street 1080p30 | street 4K30 | crowd 1080p60 | crowd 4K60 | close-up 4K30 | Tears 1080p24.

### Decode and playback

| metric | street 1080p30 | street 4K30 | crowd 1080p60 | crowd 4K60 | close-up 4K30 | Tears 1080p24 |
|---|---|---|---|---|---|---|
| budget per frame | 33.4 ms | 33.4 ms | 16.7 ms | 16.7 ms | 33.4 ms | 41.7 ms |
| ReadNext ms per frame, mean | 3.86 [3.34 .. 4.03] | 15.77 [13.44 .. 18.51] | 4.09 [4.08 .. 4.81] | 13.43 [13.29 .. 13.73] | 13.78 [13.28 .. 15.63] | 2.54 [2.25 .. 2.78] |
| ReadNext ms per frame, p99 | 5.6 | 19.3 | 5.5 | 17.3 | 16.2 | 4.3 |
| real-time headroom | 8.03x [7.73 .. 9.32] | 1.98x [1.67 .. 2.32] | 3.77x [3.24 .. 3.78] | 1.16x [1.14 .. 1.18] | 2.28x [1.99 .. 2.35] | 15.35x [13.99 .. 17.26] |
| grab only (decompress), ms | 1.85 | 6.98 | 1.83 | 3.84 | 2.70 | 0.51 |
| colour conversion, ms | 1.28 | 7.04 | 1.58 | 7.03 | 8.85 | 1.33 |
| new frame buffer, ms | 0.64 | 2.77 | 0.53 | 2.59 | 3.23 | 0.57 |
| logical processors busy: ReadNext / grab-only loop | 3.6 / 5.9 | 3.2 / 6.7 | 3.5 / 6.0 | 3.1 / 9.5 | 2.5 / 10.9 | 2.5 / 9.1 |
| playback, 3-frame buffer: presented fps | 30.0 | 30.0 | 60.0 | 59.9 [26.2 .. 60.0] | 30.0 | 24.0 |
| playback, 3-frame buffer: late drops | 0 / 420 | 0 / 420 | 0 / 892 | 0 [0 .. 483] / 892 | 0 / 358 | 0 / 1800 |
| presentation lateness p50 / p99, ms | 6.9 / 14.6 | 7.0 / 15.2 | 6.6 / 15.0 | 6.7 / 14.8 | 6.8 / 14.7 | 6.9 / 14.8 |
| frames presented > half a frame late | 0 | 2 | 351 | 337 | 0 | 0 |
| playback, 1-frame buffer (pass 1): late drops | 0 | 0 | 0 | 129 | 0 | 0 |
| playback, 1-frame buffer: lateness p99 / max, ms | 14.7 / 17.2 | 15.0 / 15.1 | 15.3 / 16.4 | 52.1 / 83.6 | 14.8 / 15.6 | 14.8 / 15.6 |

### Seek and storyboard

| metric | street 1080p30 | street 4K30 | crowd 1080p60 | crowd 4K60 | close-up 4K30 | Tears 1080p24 |
|---|---|---|---|---|---|---|
| keyframe interval (GOP), frames | 250 | 250 | 250 | 250 | 90 | 18 |
| seek + read, median ms (40 random frames) | 243 [162 .. 277] | 836 [552 .. 1002] | 246 [185 .. 250] | 604 [509 .. 823] | 261 [251 .. 275] | 25 [25 .. 37] |
| seek + read, p90 ms | 461 | 1562 | 442 | 994 | 385 | 32 |
| storyboard: tiles / build time | 8 / 2.37 s | 8 / 8.83 s | 8 / 2.75 s | 8 / 6.10 s | 6 / 1.58 s | 38 / 1.02 s |
| storyboard tile memory | 486 KiB | 486 KiB | 486 KiB | 486 KiB | 365 KiB | 1710 KiB |

### AI scan (GPU, DirectML; every frame unless marked 5/s)

| metric | street 1080p30 | street 4K30 | crowd 1080p60 | crowd 4K60 | close-up 4K30 | Tears 1080p24 |
|---|---|---|---|---|---|---|
| UltraFace analyzed frames/s | 167.5 [112.3 .. 266.7] | 52.0 [38.2 .. 54.9] | 196.2 [185.2 .. 223.5] | 64.0 [62.1 .. 69.3] | 70.1 [34.6 .. 71.5] | 250.4 [123.6 .. 266.3] |
| YOLO11n analyzed frames/s | 127.7 [57.0 .. 171.7] | 48.9 [38.3 .. 52.5] | 157.3 [145.7 .. 177.6] | 64.3 [59.1 .. 69.8] | 68.0 [49.6 .. 68.9] | 167.4 [104.3 .. 177.5] |
| YOLO26l analyzed frames/s (pass 1) | 13.1 | 12.7 | 13.3 | 12.0 | 13.1 | 13.5 |
| mean Detect ms: UltraFace / YOLO11n / YOLO26l | 4.7 / 6.8 / 75.7 | 11.5 / 11.1 / 75.6 | 4.6 / 5.8 / 74.7 | 10.8 / 8.8 / 81.0 | 8.5 / 7.9 / 74.8 | 3.8 / 5.7 / 74.0 |
| faces per frame: UltraFace / YOLO11n / YOLO26l | 4.2 / 9.1 / 25.0 | 4.2 / 9.1 / 26.0 | 8.4 / 16.2 / 26.0 | 8.4 / 16.2 / 26.3 | 3.0 / 3.0 / 3.0 | 0.6 / 0.6 / 0.6 |
| tracks: UltraFace / YOLO11n / YOLO26l | 138 / 287 / 786 | 142 / 292 / 828 | 326 / 444 / 822 | 324 / 420 / 763 | 4 / 3 / 15 | 17 / 10 / 35 |
| YOLO11n queue max / live frames peak | 4/4 / 6 | 1/4 / 3 | 4/4 / 6 | 1/4 / 2 | 1/4 / 1 | 4/4 / 6 |
| GPU busiest engine, this process: UltraFace / YOLO11n / YOLO26l | 5 / 19 / 63 % | 2 / 11 / 61 % | 6 / 26 / 70 % | 6 / 19 / 61 % | 3 / 14 / 65 % | 10 / 31 / 73 % |
| app default, YOLO11n at 5/s: frames analyzed | 70 | 70 | 75 | 75 | 60 | 375 |
| app default: speed vs real time | 13.23x | 4.00x | 11.51x | 3.30x | 7.91x | 26.02x |
| app default: tracks | 124 | 129 | 117 | 122 | 3 | 7 |

Dedicated GPU memory per model, the same on every clip and in every pass: UltraFace 86 MiB, YOLO11n 157 MiB, YOLO26l
1075 MiB.

### Export (YOLO11n detections blurred, FFmpeg h264_mf)

| metric | street 1080p30 | street 4K30 | crowd 1080p60 | crowd 4K60 | close-up 4K30 | Tears 1080p24 |
|---|---|---|---|---|---|---|
| steady frames/s (10-90 %) | 99.8 [84.5 .. 139.7] | 20.1 [19.6 .. 23.5] | 144.1 [136.7 .. 148.2] | 32.5 [26.8 .. 33.4] | 41.7 [29.0 .. 45.7] | 250.3 [128.2 .. 258.2] |
| total frames/s (incl. start-up and finish) | 90.4 | 20.2 | 134.6 | 31.6 | 39.6 | 233.9 |
| ms per frame, steady | 10.0 | 49.8 | 6.9 | 30.7 | 24.0 | 4.0 |
| speed vs real time | 3.33x | 0.67x | 2.40x | 0.54x | 1.39x | 10.43x |
| frames with blur | 420 | 420 | 892 | 892 | 358 | 631 of 1,800 |
| output size / bitrate | 11.4 MiB / 6.8 Mbit/s | 43.2 MiB / 25.9 | 24.0 MiB / 13.5 | 92.4 MiB / 52.1 | 33.8 MiB / 23.7 | 30.9 MiB / 3.5 |
| logical processors: app / encoder / both | 4.7 / 2.8 / 7.5 | 3.3 / 2.9 / 6.2 | 6.5 / 4.7 / 11.2 | 8.7 / 5.1 / 13.8 | 4.5 / 4.8 / 9.4 | 4.3 / 5.5 / 9.8 |
| encoder process (ffmpeg.exe) peak private | 725 MiB | 2214 MiB | 766 MiB | 2378 MiB | 2378 MiB | 1098 MiB |
| app process private increase | 102 MiB | 544 MiB | 114 MiB | 632 MiB | 570 MiB | 134 MiB |
| live frames peak (design bound 11) | 11 | 6 | 11 | 9 | 7 | 11 |

### Memory per stage (app process): increase over the settled level before the stage, MiB

| stage | street 1080p30 | street 4K30 | crowd 1080p60 | crowd 4K60 | close-up 4K30 | Tears 1080p24 |
|---|---|---|---|---|---|---|
| decode | 143 | 478 | 89 | 488 | 476 | 48 |
| decode, grab only (no frames kept) | 121 | 406 | 59 | 410 | 404 | 28 |
| playback | 96 | 581 | 171 | 588 | 580 | 69 |
| AI UltraFace | 311 | 712 | 401 | 792 | 715 | 285 |
| AI YOLO11n | 428 | 801 | 435 | 838 | 828 | 449 |
| AI YOLO26l (pass 1) | 1465 | 1929 | 1375 | 1867 | 1861 | 1462 |
| export | 102 | 544 | 114 | 632 | 570 | 134 |

Live native frames, exact peak over all passes / design bound: decode and seek 1 / 1; playback 5 / 5 (1-frame
buffer 3 / 3); AI at most 6 / 6; export at most 11 / 11. No stage exceeded its bound.

### Leak check (each model 5 times in one process, first 150 frames of street 1080p30, settled private bytes)

| model | before | after runs 1 / 2 / 3 / 4 / 5 |
|---|---|---|
| UltraFace | 359 MiB | 502 / 534 / 426 / 501 / 534 MiB |
| YOLO11n | 530 MiB | 596 / 625 / 601 / 650 / 638 MiB |
| YOLO26l | 638 MiB | 642 / 645 / 646 / 571 / 645 MiB |

The first run of a model adds a one-time amount (UltraFace +143 MiB, YOLO11n +66 MiB, YOLO26l +4 MiB); later runs
go up and down rather than climb. The settled level right after an AI stage is also well above the level measured
before the next stage, a few seconds later: on street 1080p30 in pass 1, 373 then 240 MiB after UltraFace, and 494
then 237 MiB after YOLO26l. Native memory (ONNX Runtime / DirectML, the C runtime heap) is released with a delay; the
benchmark shows the delay, not which component causes it.

## Caveats

- **The machine was in use.** Other apps were open (an IDE, a browser and more). Before each stage the harness waited for other processes to drop below 4 logical processors,
  but they still used up to 11.3 during a stage (crowd 1080p60 decode), and 9.5 during the 4K60 playback pass that
  dropped 483 frames. The ranges in brackets show this; medians are the fairer single number. Some ranges are wide:
  YOLO11n on street 1080p30 ran at 57.0 to 171.7 analyzed frames/s, and street 1080p30 export at 84.5 to 139.7.
- YOLO26l and the 1-frame-buffer playback ran once (pass 1).
- No ground truth: face and track counts compare the models with each other, not with what is really in the video.
- Playback stops at the benchmark's sink. The WPF bitmap upload and screen composition are not measured.
- The export's redaction stage is not timed on its own, so the 4K export bottleneck (finding 4) is inferred from the
  live-frame peaks and CPU use.
- CPU averages for very short loops (grab only on the 1080p clips, about 1 s) are coarse.
- The 30 fps and 60 fps clips are different videos (content, bitrate, faces).

## How it was measured

**Command** (Release build; the build treats warnings as errors):

```
dotnet run -c Release --project tools/VisionRedact.Benchmarks -- --compare media/pexels/street-flags_1080p30.mp4 media/pexels/street-flags_4k30.mp4 media/pexels/crowd-crossing_1080p60.mp4 media/pexels/crowd-crossing_4k60.mp4 media/pexels/group-closeup_4k30.mp4 media/tears_of_steel_1080p.mov
```

**Hardware and state**: 22 logical processors (Intel Core Ultra 9 185H; the CPU name is not printed by the harness),
63.5 GiB RAM, .NET 10.0.12, AI on the NVIDIA RTX 3500 Ada Generation Laptop GPU (11.7 GB) through DirectML. AC power
(battery 100 %), power plan "High performance", Windows power mode "Balanced". 17.4 GiB of physical memory free at
the start; 3.1 logical processors busy in other processes before the run. Export encoder: FFmpeg `h264_mf` ->
"H264 Encoder MFT" {6ca50344-051a-4ded-9779-a43305165e35}, a software transform. NVENC probe failed (driver too old
for this FFmpeg build).

**Order and repetitions**: one unreported warm-up pass first (every stage and every model on the first 90 frames of
street 1080p30), so the JIT compiler and the GPU shaders are warm when measuring starts. Then 3 measured passes over
all six videos, with the order rotated by one video each pass, so no clip always runs first. YOLO26l and the 1-frame
buffer playback run in pass 1 only. Stages never overlap, and each needs at least 8 GiB of free physical memory
(no stage was skipped and none failed). Each export file is deleted as soon as its size is read.

**Frames**: the first 1,800 frames of each video in every stage. Only Tears is longer: 1,800 of its 17,620 frames.

**What each number times**:
- *Decode*: 30 warm-up frames, then a stopwatch around every `ReadNext` until the end of the clip. fps = frames /
  loop time. CPU = this process's CPU time during the loop / loop time (logical processors busy on average).
  *Grab only* is the same walk with `Skip()`. *One reused buffer* reads with OpenCV directly (same FFmpeg backend as
  the app's decoder) into one buffer that is reused.
- *Seek*: `Seek` + `ReadNext` to 40 random frames, `Random(42)`, the same targets for every video.
- *Storyboard*: the app's `StoryboardJob`, its own elapsed time.
- *Playback*: the app's `PlaybackSession` from Play to Ended. A sink records when each frame arrives and disposes
  it. Lateness is the arrival time minus the due time (first frame after Play + frame number / fps).
- *AI scan*: the app's `DetectorHost` + `AnalysisJob` + `IouObjectTracker`, timed from job start until the job
  completes (detected on the completion itself, not by polling). This includes the job opening its own decoder; it
  excludes loading the model and 5 warm-up Detect calls. Thresholds as in the app: confidence 0.7 (UltraFace), 0.35
  (YOLO11n), 0.3 (YOLO26l); NMS IoU 0.4; minimum box 0.004. The main runs analyze every frame (a stress test). The
  "5/s" rows use the app's default, TargetRateSampling(5, fps).
- *Export*: the app's `VideoExportService` with the FFmpeg writer, blurring the YOLO11n every-frame detections.
  *Total* is the service's own elapsed time: audio probe, starting ffmpeg, encoding, flushing and moving the MP4
  index to the front. *Steady* uses the progress reports (every 30 frames) between 10 % and 90 % of the frames.
- *CPU*: process times of this process and of its child processes (`ffmpeg.exe`, found by parent process id; their
  CPU time is read through a handle kept open, so it is exact even after they exit), and system times for everything
  else. GPU engine use and dedicated GPU memory per process come from the Windows performance counters Task Manager
  uses. Memory: private bytes (committed memory) of the app process, sampled every 100 ms; the child process's
  lifetime peak. Live frames: an exact high-water mark kept by `OwnedVideoFrame`, reset at the start of each stage.

**Units**: MiB = 1,048,576 bytes, KiB = 1,024 bytes, GiB = 1,073,741,824 bytes; Mpx = million pixels; Mbit/s =
1,000,000 bits per second. Values are rounded to the digits shown.

## Words used above

- **Decode / encode**: decompress a video frame into a picture / compress pictures back into video (H.264 here).
- **BGR, colour conversion**: the decoder outputs YUV (brightness plus colour at quarter resolution); OpenCV and the
  app work in BGR, 3 bytes per pixel. Converting every pixel is the colour conversion.
- **Headroom**: decode speed divided by the video's frame rate. Below 1.0x, playback cannot keep up.
- **Decode-ahead buffer**: frames decoded before they are needed, so a slow frame does not make the picture late.
- **Late drop**: a frame skipped because its time on screen had already passed (more than one frame late).
- **Keyframe / GOP**: a keyframe is stored whole; the frames after it store only changes. The GOP is the distance
  between keyframes. A seek decodes from the keyframe before the target.
- **Queue / live frames**: frames waiting between pipeline stages. Each stage's queue has a fixed size, so the number
  of frames in memory has an upper bound (the design bound) whatever the video's length.
- **Private bytes**: memory a process has committed for itself (not shared). **VRAM**: memory on the graphics card.
- **Logical processors busy**: average number of CPU threads kept busy (22 available on this laptop).
- **Software vs hardware encoder**: a software encoder runs on the CPU cores; a hardware encoder (NVENC, Quick Sync)
  uses a dedicated chip.
