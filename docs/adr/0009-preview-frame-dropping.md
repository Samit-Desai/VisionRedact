# ADR-0009: Preview May Drop Stale Frames; Export Never Does

> **Update (2026-10-11):** the "skipped frames are not decoded for display" cost below was the intent, but the
> worker decoded and redacted every frame and only then dropped the late ones, so a drop saved no work. On 4K60 with
> preview redactions (≈ 29 ms of work per 16.7 ms frame) playback showed 1 frame in 5 and ran 1.8× slower than real
> time. Fixed: the decode task now skips a frame that would be late *before* converting or redacting it
> (`IVideoDecoder.Skip`), the cap of 4 counts every frame left out since the one on screen, and the clock restarts
> from the frame shown after the cap, so lateness cannot pile up. Details and numbers: `docs/VideoPipeline.md`.

## Context
Decode time varies (keyframes, 4K, CPU load) and the UI thread can be busy (layout, input). Preview must stay
real-time and responsive.

## Options
1. Present every frame, slowing playback when behind.
2. Queue frames for the UI (grows when the UI is slow).
3. Drop frames whose display time has passed; keep only the newest for the UI.

## Decision
Option 3 for preview only: the worker skips overdue frames (`PlaybackClock.IsLate`, max 4 consecutive), and the
presenter keeps a single-slot mailbox, disposing any frame replaced before display. Both are counted.
Export and analysis share none of this code.

## Why
A late preview frame has no value — showing it makes the video lag behind its own clock. (1) turns load into
slow motion; (2) turns load into ever-growing latency and memory.

## Benefits
Bounded latency and memory; the UI can never be flooded.

## Costs
Visible stutter on overloaded machines; skipped frames are not decoded for display.

## Risks
Drop logic leaking into correctness-critical paths.

## Mitigation
Dropping lives in `PlaybackSession`/presenter only; export has its own loop with a frame-count check; drop counters
are exposed in diagnostics.

## When to reconsider
If users need guaranteed every-frame review at reduced speed, add an explicit "every frame" review mode.
