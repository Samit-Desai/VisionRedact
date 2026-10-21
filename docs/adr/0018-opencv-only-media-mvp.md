# ADR-0018: OpenCV-Only Media Stack for the MVP

> **Update (2026-09):** the export side is superseded by [ADR-0020](0020-ffmpeg-exporter.md) — an FFmpeg writer
> (external process) now provides faster H.264 and audio when FFmpeg is installed; OpenCV's writer remains the
> fallback. Decoding is still OpenCV, and the decode-side limitations below still apply.

## Context
OpenCV's videoio (FFmpeg backend) is used for decoding now and `VideoWriter` for export later. It is a convenience
layer over FFmpeg that hides container details.

## Options
1. OpenCV videoio only (accept limitations).
2. FFmpeg (FFmpeg.AutoGen) for demux/decode/encode/mux from the start.
3. Media Foundation for decode, OpenCV for processing.

## Decision
Option 1 for the MVP, with limitations documented and isolated behind `IVideoDecoder` / `IVideoExportService`.

## Known limitations (honest list)
- **Constant frame rate assumed**: time = index / fps. VFR sources can drift.
- **Frame count is container metadata**; may be approximate — the pipeline tolerates early/late end of stream.
- **Seek cost** grows with GOP length; accuracy depends on the backend.
- **End of stream and decode error are indistinguishable** from `VideoCapture.Read` returning false.
- **No audio**: export will drop audio; no remux.
- **Limited codec/bitrate control** on `VideoWriter`; H.264 availability depends on the build.
- **No hardware decode/encode control** from our code.

## Why
The core engineering (threading, ownership, pipelines, UI) is independent of the codec layer; a full
FFmpeg integration would consume the schedule without improving those areas.

## Benefits
Small surface, fast delivery, one native dependency.

## Costs
Not suitable for evidentiary production output (audio, exact timestamps).

## Mitigation
Adapters make the swap local; a production design would use FFmpeg or Media Foundation for demux/decode with PTS,
hardware decoding (DXVA/D3D11VA/NVDEC), hardware encoding (NVENC/QSV/AMF), and remux of the original audio.

## When to reconsider
As soon as audio, VFR accuracy, hardware acceleration or delivery formats are requirements.
