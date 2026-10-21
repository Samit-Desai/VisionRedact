# ADR-0002: Emgu CV as the OpenCV Integration Layer

## Context
We need frame-accurate decode/seek, pixel access for AI and redaction, image operations (blur, pixelate, resize) and
video writing from C#, on Windows x64.

## Options
1. WPF `MediaElement` / Media Foundation directly — playback only, no practical per-frame pixel pipeline.
2. FFmpeg via P/Invoke (FFmpeg.AutoGen) — maximum control, very low-level (packets, frames, sws_scale), large surface.
3. OpenCvSharp — popular OpenCV wrapper.
4. Emgu CV — mature OpenCV wrapper with official runtime packages (`cvextern.dll` + FFmpeg videoio plugin).

## Decision
Emgu CV 4.13 (`Emgu.CV` + `Emgu.CV.runtime.windows`), accessed only from Infrastructure projects.

## Why
One library covers decode, seek, image processing and writing, so the MVP spends its time on architecture rather than
codec plumbing. Emgu's native runtime package makes deployment reproducible (pinned managed + native versions).
OpenCvSharp was excluded by project constraint; FFmpeg.AutoGen is the right tool for production-grade muxing/audio,
which is out of MVP scope (ADR-0018).

## Benefits
Mature, documented, same API across decode/process/encode; `Mat` gives zero-copy pointer access.

## Costs
~80 MB of native binaries; x64-only deployment; GPL/commercial dual licence; OpenCV videoio hides stream details
(PTS, audio, VFR).

## Risks
Native crashes cannot be caught; managed/native version mismatch; licence for commercial use.

## Mitigation
Adapter isolation (swap to FFmpeg/MF without touching Application); startup probe + exception translation; central
package versions; input validation before native calls; licence noted in README.

## When to reconsider
When audio preservation, VFR accuracy, hardware decode/encode or commercial licensing become requirements.
