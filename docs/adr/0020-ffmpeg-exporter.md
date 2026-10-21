# ADR-0020: FFmpeg Exporter (External Process) for H.264 and Audio

> **Correction (2026-10-11):** this ADR first described `h264_mf` as selecting a hardware encoder. As the app calls it
> (FFmpeg's default `-hw_encoding 0`), Media Foundation loads Microsoft's **software** "H264 Encoder MFT", which runs on
> the CPU — verified with FFmpeg's verbose log and Windows' GPU engine counters (no GPU engine busy; ~4 cores at 1080p60,
> ~6–7 at 4K60 on a test pattern). The measured speed-up comes from that encoder being much faster than OpenH264, not
> from hardware. With driver 596.41 no hardware encoder was used (NVENC needed driver 610 or newer). Since the update
> to 610.88 NVENC passes the probe, but it measured 17–20 % slower than `h264_mf` in the pipeline at 1080p and equal at
> 4K60 (PC in use), so `h264_mf` was moved to the front of the probe order. Intel Quick Sync works but comes after
> `h264_mf`, so it is never tried. Details: [Performance.md, optimization 7](../Performance.md).

Supersedes the export part of [ADR-0018](0018-opencv-only-media-mvp.md). Decoding stays on OpenCV.

## Context
After the export pipeline was parallelized (decode ‖ redact ‖ encode), export became **encoder-bound**: OpenCV's
`VideoWriter` can only use OpenH264 (measured ~75 frames/s at 1080p), writes no audio and offers
no bitrate control (≈48 Mbit/s output). Measured probes showed OpenCV's bundled FFmpeg exposes no hardware encoder
(every `VIDEOWRITER_PROP_HW_ACCELERATION` request failed). The 23-minute 1080p59.94 test file took ~19 minutes to
export, with no sound.

## Options
1. Keep OpenCV `VideoWriter` (status quo).
2. FFmpeg libraries in-process via P/Invoke (FFmpeg.AutoGen / Sdcb.FFmpeg): full control, no process boundary.
3. **`ffmpeg.exe` as an external process**, raw frames streamed over stdin.
4. Media Foundation Sink Writer directly (Windows-only COM, hardware MFTs, AAC encoder).

## Decision
Option 3. `FfmpegVideoWriterFactory` (Infrastructure.Video) implements the existing `IVideoWriterFactory`:
- raw `bgr24` frames are written to ffmpeg's stdin straight from native frame memory (a span, no managed copy);
- the H.264 encoder is **probed once per process** in preference order `h264_mf` (Media Foundation; as called,
  Microsoft's software H.264 encoder on the CPU) → `h264_nvenc` → `h264_qsv` → `h264_amf` → `libopenh264`, by encoding
  a few test frames (Media Foundation first because it measured faster than NVENC inside the pipeline);
- the source's first audio stream is aligned to the first video frame (ffprobe start times → `adelay`/`atrim`),
  encoded to AAC, padded (`apad`) and cut with `-shortest` so it ends with the video;
- bitrate ≈ 0.1 bit/pixel/frame (12.4 Mbit/s at 1080p60), `+faststart`;
- falls back to the OpenCV writer when ffmpeg.exe is absent, for AVI, or when no H.264 encoder works.

`IVideoWriter` gained `Complete()`: closing stdin, waiting for exit and checking the exit code can fail, so finishing a
file is an explicit step; `Dispose()` without `Complete()` means abort (the process is killed, the caller deletes the
temp file).

The build is FFmpeg 9.0 **LGPL, shared** (BtbN FFmpeg-Builds), pinned by SHA-256 in `scripts/download-ffmpeg.ps1`,
installed into `ffmpeg/` (git-ignored) and copied next to the app by the build.

## Why
- **Measured**: first 1,800 frames of the real MTS, blur on every frame — OpenCV writer 72–80 frames/s, 148 MB, no
  audio; FFmpeg writer (`h264_mf` + AAC) **147–167 frames/s, 45 MB, with audio** (≈2× faster, ≈3× smaller). The whole
  23-minute file: ~19 min → ~9 min.
- **Process isolation**: a crash or leak in native codec code cannot take down the WPF app; no binding layer to
  maintain; the LGPL binaries are used unmodified, which keeps licence compliance simple.
- **Seam already existed**: the export pipeline only knows `IVideoWriter`; this is one new infrastructure class plus a
  registration change. Nothing in Application or Presentation changed except the `Complete()` call.

## Benefits
A much faster H.264 encoder than OpenH264 (Microsoft's software MFT), hardware encoders (NVENC, Quick Sync, AMF) where
drivers and the probe order allow, audio in exports, controlled bitrate, smaller files, automatic encoder selection,
graceful fallback.

## Costs
- One pipe copy per frame (6 MB at 1080p) — measured not to be the bottleneck.
- A second native stack (≈180 MB of FFmpeg binaries) to download and version; the app now runs an external process
  whose lifetime it must manage (kill on cancel, exit-code checks, stderr capture).
- Encoder selection depends on drivers and on the probe order: Media Foundation's software encoder comes first
  (measured faster than NVENC in the pipeline at 1080p, equal at 4K60, PC in use), so on Windows the hardware encoders
  are only reached when it fails; it uses CPU cores that a hardware encoder would leave free.
- Audio is re-encoded to AAC (camcorder AC-3 is not reliably playable in MP4); subtitles are not carried over.

## Risks and mitigation
| Risk | Mitigation |
|---|---|
| ffmpeg missing or corrupt | factory falls back to OpenCV; download script verifies SHA-256 |
| encoder present but broken on this machine | per-process probe with a real encode; next candidate |
| ffmpeg exits mid-export (disk full, bad args) | broken pipe → `ExportException` with ffmpeg's stderr tail; temp file deleted |
| orphaned ffmpeg after cancel | `Dispose()` kills the process tree; integration test asserts no process remains |
| audio longer/shorter than exported video | `apad` + `-shortest`; test asserts durations within 0.1 s |
| audio/video offset | start-time alignment; test asserts a 0.5 s offset is preserved |
| "latest" build rebuilt upstream | pinned hash fails loudly; the script explains how to re-pin |

## When to reconsider
- Hardware **decode** becomes the bottleneck → an FFmpeg-based `IVideoDecoder` (same process approach or libav*).
- Smart rendering (copy untouched GOPs, re-encode only redacted segments) → needs in-process libav* (option 2).
- Evidentiary requirements (bit-exact timestamps, metadata preservation) → full FFmpeg demux/remux path.
