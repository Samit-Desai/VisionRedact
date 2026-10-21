# ADR-0013: Frame Sampling Strategies

## Context
Inference is the slow stage of analysis (YOLO11n 23.4 ms p50 on CPU). A 1080p59.94 AVCHD file has 82,351 frames;
analyzing every one is often unnecessary because faces move little between adjacent frames. But what is not analyzed
must still be redacted on every frame, and the result must not depend on machine speed.

## Options
1. Always analyze every frame.
2. Drop frames under load (`DropOldest` on the analysis channel).
3. An explicit, deterministic sampling strategy chosen by the user; boxes from a sampled frame cover the frames until
   the next sample.
4. Option 3, plus interpolation of each tracked box between two samples.

## Decision
Option 3 from slice 4, extended to option 4 in 2026-09.
[`IFrameSamplingStrategy`](../../src/VisionRedact.Application/Analysis/FrameSampling.cs) has three implementations:
`EveryFrameSampling`, `EveryNthFrameSampling(n)` and `TargetRateSampling(targetFps, sourceFps)` (rational stepping, no
drift). Each exposes `HoldFrames` (sampling interval − 1), which
[`AnalysisJob`](../../src/VisionRedact.Application/Analysis/AnalysisJob.cs) passes to the detection repository. The
producer calls `IVideoDecoder.Skip()` (grab without retrieving pixels) for unsampled frames, so only sampled frames
become `OwnedVideoFrame`s in the channel.
[`InMemoryDetectionRepository.GetForDisplay`](../../src/VisionRedact.Application/Detections/InMemoryDetectionRepository.cs)
returns the nearest analyzed frame's detections within the hold window and, when the same track (and class) appears
on the next analyzed frame, moves each box towards it with `NormalizedRect.Lerp`. Boxes with no match on the next
sample are held unchanged.

## Why
Dropping under load (2) would make coverage a function of CPU speed — unacceptable for redaction. An explicit strategy
makes coverage a documented, reproducible choice. `Skip()` makes sampling actually cheap: on the real MTS, skipping
unsampled frames instead of `ReadNext` raised throughput from 272 to 615 frames/s (2.3×). Holding alone lets a moving face drift out of
its box for up to one interval (≈ 11 frames at 5 samples/s of 59.94 fps); interpolating a tracked box follows it.

## Benefits
Measured with benchmark B8 (2.MTS, first 3,600 frames, 1-in-12 sampling, ground truth = YOLO on every frame, 9,963
faces, 9,135 of them between samples):

| Between-sample boxes | Tracks | Coverage | Centre error p50 | Visible drift > 8 px |
|---|---|---|---|---|
| Hold | – | 78.8 % | 2.5 px | 17.9 % |
| Interpolate, IoU + prediction only | 262 | 78.9 % | 1.1 px | 13.8 % |
| Interpolate, prediction + distance fallback | 80 | 83.0 % | 1.0 px | 4.3 % |

Preview and export both read `GetForDisplay`, so they get the same boxes.

## Costs
A short-lived object seen only between samples is missed. The remaining ~17 % uncovered faces are frames where the
detector missed the face on the sampled frame (detector flicker), not drift — interpolation cannot fix those.
Interpolation depends on tracking quality (ADR-0016).

## Risks
Interpolation moving a box away from a face that actually stopped or changed direction mid-interval; a hold window
covering frames that were never analyzed after cancellation.

## Mitigation
Interpolation is linear over at most one interval (⅕ s at 5 samples/s) and only between two observations of the same
track; unmatched boxes are held, so nothing redacted stops being redacted. Padding (ADR-0015) absorbs small errors.
The hold window only extends from frames the repository marks as analyzed. Every-frame sampling remains available.

## When to reconsider
Next levers: bridge short track gaps to cover detector flicker, or sample more densely — affordable on the GPU, where
the in-app pipeline is decode-bound (ADR-0017).
