# ADR-0024: Track Ids Are Unique Across Analysis Runs

## Context
`IouObjectTracker.Reset()` restarted numbering at 1 on every analysis run, while `AddFrameResults` keeps reviewed
(approved or rejected) detections from earlier runs. After re-analysis, two different faces could share a TrackId.
`GetForDisplay` interpolates a held box towards the box with the same TrackId on the next analysed frame, so an
approved face's box could slide towards a different face. The real face would then show unblurred between samples.

## Decision
- `IDetectionRepository.HighestTrackId` is a high-water mark. It is updated when a detection is added, never lowered
  by `Remove`, cleared by `Reset` and recomputed by `Load`.
- `IObjectTracker.Reset(int firstTrackId = 1)`; `AnalysisJob` calls `Reset(repository.HighestTrackId + 1)`.

## Consequences
- TrackIds are unique per video, and person identity (ADR-0022) can rely on them.
- Ids are not dense (gaps after deletions or re-runs), which does no harm.
- Tested: the tracker starts at the seed; a second run never reuses a number from an approved track; the high-water
  mark survives removal.
