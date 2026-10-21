# ADR-0016: Detection vs DetectionTrack

## Context
A detector reports boxes per frame with no identity. Review ("reject this false positive everywhere"), interpolation
between samples (ADR-0013) and manual regions that follow a moving object all need to know that boxes on different
frames belong to the same object. Faces in wide 1080p shots are ~30 px and move half their width or more between
samples taken every ~12th frame.

## Options
1. Store tracks only; detections become points on a track.
2. Keep detections as immutable observations; tracking adds an identity (`TrackId`) and tracks are derived.
3. One visual tracker (pixel-based) for everything, AI detections included.

## Decision
Option 2 for AI detections, plus a separate pixel tracker for manual regions.
[`Detection`](../../src/VisionRedact.Domain/Detections/Detection.cs) is one observation (frame, box, confidence,
class, status) with an optional `TrackId`. [`DetectionTrack`](../../src/VisionRedact.Domain/Detections/DetectionTrack.cs)
is a summary derived from detections sharing a track id. [`IObjectTracker`](../../src/VisionRedact.Application/Tracking/IObjectTracker.cs)
may only set `TrackId`, never change boxes or confidences.
[`IouObjectTracker`](../../src/VisionRedact.Application/Tracking/IouObjectTracker.cs) associates greedily, best score
first, against each track's constant-velocity *predicted* box (smoothed velocity): IoU ≥ 0.3, or, failing that, a
centre distance ≤ 1.5 box sizes with an area ratio of 0.4–2.5. A track survives 3 analyzed frames without a match
(`MaxMissedAnalyses`). Manual regions use [`IVisualTracker`](../../src/VisionRedact.Application/Tracking/RegionTrackingJob.cs)
via `RegionTrackingJob`, implemented by
[`EmguVisualTracker`](../../src/VisionRedact.Infrastructure.Video/EmguVisualTracker.cs) with KCF as the default.

## Why
A detection is a fact; a track is an interpretation. Keeping them apart lets tracking be re-run or replaced without
touching detections, and lets review act at either level. Identity for AI output comes from boxes the detector
already produced, so it is cheap and runs on sampled frames only. A manual region has no detector behind it, so it
needs a pixel tracker; running a pixel tracker for every AI box on every frame would cost more than the detector.

## Benefits
An IoU-only tracker fragmented small fast faces (271 tracks for 1,262 detections at 5 samples/s). With prediction and
the distance fallback, B8 on 3,600 frames of 2.MTS dropped from 262 to 80 tracks, and visible drift between samples
from 13.8 % to 4.3 % once boxes were interpolated per track. Track-level approve/reject becomes meaningful.

## Costs
Greedy association can swap identities when similar faces cross. The pixel tracker is sequential and per region.
CSRT, usually the most accurate classic tracker, fails in Emgu CV 4.13.0.5924 (0/120 frames); KCF tracks 120/120 at
21 ms/frame, MIL 120/120 at 66 ms/frame.

## Risks
A wrong merge makes a track-level reject remove a real face; a visual tracker drifting onto background.

## Mitigation
Association is class-gated and size-gated; tracks close after 3 missed analyses. `RegionTrackingJob` stops when the
tracker reports the target lost and trims the region there, reporting it, instead of keeping a box that covers
nothing. CSRT stays selectable so it can be re-checked after an Emgu upgrade.

## When to reconsider
If identity swaps matter in crowds, add appearance features (re-identification embeddings) or a full Kalman/SORT
variant behind `IObjectTracker`.
