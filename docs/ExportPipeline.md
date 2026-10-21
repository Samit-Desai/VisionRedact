# Export Pipeline

## Purpose

Produce the deliverable: a redacted copy of the video — every frame, in order, at source resolution, with exactly the
redactions the reviewer approved — without ever modifying the source.

## Architecture

```text
Export pressed ─► RedactionSnapshot.Capture(detections, regions, settings)     immutable copy
               ─► ExportJob (ProcessingJob: cancellable, progress)
                     VideoExportService — three pool tasks joined by bounded channels (capacity 4, Wait):
                       decode : decoder #3, ReadNext from frame 0 — never Seek, never Skip ─► Channel
                       redact : plan = snapshot.PlanFor(frame) (RedactionPlanner, same as preview)
                                IRedactionProcessor.Apply(frame, plan) — blur / pixelate / black ─► Channel
                       encode : IVideoWriter.Write(frame); Dispose(frame)
                                MP4 with FFmpeg installed: ffmpeg.exe (stdin, raw bgr24) → H.264 (Media Foundation first) + AAC audio
                                MP4 without FFmpeg: OpenCV avc1 → mp4v · AVI: OpenCV MJPG (always)
                     IVideoWriter.Complete() — flush, close the container, surface encoder errors
                     verify frames written == frames read
                     temp file ──File.Move──► requested path   (after the count check)
```

## Why this design

- **Snapshot, not live state**: the reviewer keeps working during a multi-minute export; the output must correspond to
  one review state. Opening another video (which resets live stores) cannot corrupt a running export.
- **Separate from preview** (ADR-0007): preview frames may be dropped, scaled or stale; export uses its own decoder.
- **Same planner as preview**: what you reviewed is what you export (`RedactionPlanner` is shared).
- **Pipelined, still in order**: decode, redact and encode overlap (throughput = slowest stage, not the sum); one reader and one writer per channel keeps frames in order. Up to ~11 frames in flight.

## Correctness guarantees

| Guarantee | Mechanism | Test |
|---|---|---|
| every frame exported, in order | no seek/skip; count check | `Every_frame_is_written_in_order…`, real round trip |
| rejected detections not redacted, approved/pending are | planner | `Redactions_from_the_snapshot_are_applied_to_exactly_the_right_frames` |
| source untouched | read-only open; output ≠ source (case-insensitive full path) | SHA-256 before/after in `ExportTests`; `Source_file_can_never_be_the_output` |
| no partial output | temp name, rename on success, delete on failure/cancel | cancellation/writer-failure tests |
| encoder dropping frames is detected | `FramesWritten == read` | `Encoder_that_drops_frames_is_detected…` |
| wrong-size frames are loud | `EmguVideoWriter.Write` / `FfmpegVideoWriter.Write` throw | — |
| FFmpeg encoded every frame | frame count reported by ffmpeg (`-progress`) after `Complete()` | `Export_writes_h264_with_every_frame…` (ffprobe frame count) |
| audio lines up with the video | ffprobe start times → `adelay`/`atrim`; `apad` + `-shortest` | `…audio_aligned_to_the_video`, `Audio_that_starts_after_the_first_frame_keeps_its_offset` |

## Threading, memory, cancellation

Three pool tasks (decode, redact, encode); the decoder is used only by the decode task and the writer only by the encode
task, so neither needs a lock. A stage owns a frame until the next channel accepts it; the encode stage disposes it;
frames still buffered after cancel or failure are drained and disposed. If a stage fails it cancels the others and the
root exception is reported (not the secondary cancellation) — tested with a failing redaction stage. Cancellation ends
the job Cancelled and deletes the temp file.

**Writer lifecycle.** `Write` every frame, then `Complete()`: it flushes the encoder and closes the container, and it is
where an encoder failure surfaces (not `Dispose`). For FFmpeg, `Complete()` closes stdin, waits for the process to exit,
checks the exit code, and takes the frame count ffmpeg reports. `Dispose` without `Complete` means *abort*: the
FFmpeg writer kills the process so the caller can delete the temp file (tested: cancel leaves no file and no ffmpeg.exe
running).

## Measured throughput

`benchmark --export`, first 1,800 frames of the real 1080p59.94 AVCHD MTS, blur on every frame, pipelined export:

| Writer | Encoder | Throughput | Size | Audio |
|---|---|---|---|---|
| OpenCV (`EmguVideoWriter`) | OpenH264 (`avc1`) | 72–80 frames/s | 148 MB | no |
| FFmpeg (`FfmpegVideoWriter`) | `h264_mf` + `aac` | 147–167 frames/s | 45–46 MB | yes |

About 2× faster and 3× smaller. The whole 23-minute file (82,373 frames) goes from ≈19 min (OpenCV, H.264) to ≈9 min
(FFmpeg). Synthetic clip with OpenCV: 122–132 frames/s mp4v, 89–98 frames/s MJPEG. History: 13 frames/s before the blur
optimization, 73–75 after it, then pipelining (1.4× the sequential loop), then the FFmpeg writer — see
[Performance.md](Performance.md#optimization-6--pipelined-export-decode--redact--encode).

## FFmpeg writer (`FfmpegVideoWriterFactory`, `FfmpegVideoWriter`)

- **Install**: `scripts/download-ffmpeg.ps1` fetches FFmpeg 9.0.2 (BtbN build `ffmpeg-n9.0-latest-win64-lgpl-shared-9.0`,
  LGPL, shared: `ffmpeg.exe` + DLLs), checks its pinned SHA-256 and unpacks it into `ffmpeg/` (git-ignored). The WPF,
  benchmark and integration-test builds copy it to `<output>\ffmpeg\` (without `ffplay.exe`). Without it, export uses
  the OpenCV writer exactly as before.
- **Process, not P/Invoke**: `ffmpeg.exe` runs as a child process; raw `bgr24` frames are written to its stdin straight
  from a span over the native frame memory (no copy on our side; the pipe itself costs one copy per frame, which is not
  the bottleneck). A crash or leak in FFmpeg cannot take the app down, there is no binding layer, and the LGPL binaries
  are used unmodified. See [ManagedNativeInterop.md](ManagedNativeInterop.md).
- **Encoder choice**: probed once per process by encoding 10 test frames with each candidate, in order `h264_mf`
  (Media Foundation; as called, without `-hw_encoding 1`, it loads Microsoft's **software** "H264 Encoder MFT" on the
  CPU — forcing `-hw_encoding 1` loads Intel's Quick Sync MFT and measured slower, 339 vs 421 frames/s) → `h264_nvenc`
  → `h264_qsv` → `h264_amf` → `libopenh264`. The first that works wins; a missing driver just fails its probe.
  On the development laptop, with NVIDIA driver 596.41, NVENC failed ("Required: 13.1 Found: 13.0") and `h264_mf` was
  selected, so exports were encoded in software on the CPU. Since the driver update to 610.88 (2026-10-11) NVENC passes
  the probe too, but measured slower inside the pipeline at 1080p (17–20 %) and equal at 4K60 (PC in use), so
  `h264_mf` now comes first in the probe order and is selected. AMF fails (no AMD GPU); `h264_qsv` works but is never
  reached.
  [Performance.md, optimization 7](Performance.md).
- **Bitrate**: ≈0.1 bit per pixel per frame (12.4 Mbit/s at 1080p60), clamped to 2–60 Mbit/s.
- **Audio**: the first audio stream is aligned to the first video frame using ffprobe start times (`adelay` if it starts
  later, `atrim` if earlier), encoded to AAC 192 kbit/s (MP4 players do not reliably play the AC-3 that camcorders
  record). `apad` + `-shortest` make the audio end with the video. The sample MTS has AC-3 stereo 48 kHz audio
  starting at the same time as the video (0.889 s both).
- **Container**: MP4 with `+faststart` (index at the front). Frame rate passed exactly (`60000/1001`, not `59.94`).
- **Not carried over**: subtitles and data streams. AVI export stays on OpenCV Motion-JPEG.

## OpenCV / Emgu media limitations (honest list)

These apply to the OpenCV writer, i.e. AVI export, and MP4 export when FFmpeg is not installed.

- **No audio**: `VideoWriter` writes a video stream only.
- **Codecs**: OpenCV's bundled FFmpeg has no GPL encoders; H.264 goes through Cisco's OpenH264, loaded at run time as
  `openh264-1.8.0-win64.dll` from the exe folder. It is not committed (Cisco's licence covers royalties only for
  binaries users download from Cisco): `scripts/download-openh264.ps1` fetches it, checks its Cisco Authenticode
  signature, and the WPF build copies it next to the app. Verified: with the DLL, `avc1` opens and the file reads back
  as `h264`. Without it: `avc1` fails → `mp4v` (MPEG-4 Part 2). Emgu's constructor *throws* instead of returning an
  unopened writer — handled, and a codec proven unavailable is skipped for the rest of the process. Note: a process
  started as `dotnet app.dll` searches `dotnet.exe`'s folder, not the app's, so the DLL is not found there.
- **No bitrate/quality control**, no hardware encoding, no container metadata/timestamps carried over.
- **CFR output**: frame rate from source metadata; VFR sources are re-timed.

The FFmpeg writer removes the audio, bitrate and hardware-encoding limits for MP4. Still open: decoding is OpenCV's
(CPU, no hardware decode), and timestamps are CFR from metadata.

## Failure behaviour

| Failure | Kind | User sees |
|---|---|---|
| output folder missing / invalid path | `InvalidOutputPath` | dialog, nothing written |
| output == source | `SameAsSource` | dialog |
| no encoder | `EncoderUnavailable` | "Try AVI (Motion-JPEG)" |
| access denied | `AccessDenied` | dialog |
| frames lost | `IncompleteOutput` | dialog, temp deleted |
| ffmpeg.exe exits early (broken pipe) or with a non-zero exit code | `IncompleteOutput`, message includes the tail of ffmpeg's stderr | dialog, temp deleted |
| ffmpeg.exe missing, cannot start, or no H.264 encoder passes the probe | — (not an error) | export uses the OpenCV writer |
| cancel | job Cancelled | "No partial file was left behind" |

## Future extension

re-measuring the encoder order on an idle PC (NVENC measured slower at 1080p with the PC in use); hardware decode (likely the next bottleneck, not yet profiled per stage);
burned-in audit watermark; export presets; subtitle passthrough.
