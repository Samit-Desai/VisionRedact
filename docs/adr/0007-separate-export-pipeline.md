# ADR-0007: Export as an Independent Pipeline

> **Update (2026-09):** the export pipeline is now three overlapped stages (decode ‖ redact ‖ encode over bounded
> channels, still in order — [Performance.md, optimization 6](../Performance.md)), and MP4 export goes through FFmpeg
> with H.264 (Media Foundation's software encoder on the reference laptop) and the source audio when installed ([ADR-0020](0020-ffmpeg-exporter.md)). The decision below —
> export is independent of preview and never drops a frame — is unchanged.

## Context
Export produces the evidentiary output: every frame, source resolution, redactions applied, source untouched.

## Options
1. Record what the preview shows.
2. Reuse the analysis pipeline with a writer stage.
3. Dedicated sequential export job with its own decoder.

## Decision
Option 3: decode from frame 0 sequentially (no seeking), look up approved redactions per frame, apply effects at
source resolution, write with `VideoWriter` to a temporary file, verify frame count, then rename.

## Why
Preview frames can be dropped, scaled, and overlaid — using them (1) would silently lose frames. Analysis (2) samples
and optimizes for throughput. Export optimizes for correctness and determinism; mixing policies invites a
"drop when behind" branch reaching export.

## Benefits
Deterministic, testable (`frames out == frames in`); cancellation leaves no partial output; source opened read-only.

## Costs
A full additional decode pass; export time ≈ decode + effects + encode.

## Risks
OpenCV `VideoWriter` codec availability varies; no audio (ADR-0018).

## Mitigation
Probe writer backends; fall back to MJPG/AVI; document audio loss; production path via FFmpeg muxing.

## When to reconsider
When hardware encode or audio passthrough is required.
