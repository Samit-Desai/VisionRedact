# ADR-0014: Human Review — AI Results Are Proposals, Pending Is Redacted

## Context
Detectors produce false positives (a ball, a pattern) and false negatives (a turned head, a tiny face, a name badge).
A redaction tool is judged by what it lets through: a missed face in a released video is a privacy incident; an extra
blurred patch is cosmetic. Reviewers work on tens of thousands of detections per video and cannot inspect each one.

## Options
1. Trust the model: redact every detection, no review.
2. Opt-in: nothing is redacted until a reviewer approves it.
3. Opt-out (fail-safe): every detection is redacted unless a reviewer rejects it; manual regions for what AI missed.

## Decision
Option 3. [`Detection`](../../src/VisionRedact.Domain/Detections/Detection.cs) carries `ReviewStatus`
(`Pending` default, `Approved`, `Rejected`); `IsRedacted` is `Status != Rejected`.
[`RedactionPlanner`](../../src/VisionRedact.Application/Redaction/RedactionPlanner.cs) skips only rejected detections.
Review acts on single detections or whole tracks:
[`DetectionsViewModel`](../../src/VisionRedact.Presentation.Wpf/Features/Detections/DetectionsViewModel.cs) offers
approve/reject for the selected track and "approve all pending", implemented as
`IDetectionRepository.UpdateStatusWhere(...)`. False negatives are covered by manual
[`RedactionRegion`](../../src/VisionRedact.Domain/Redaction/RedactionRegion.cs)s: a normalized box over an inclusive
frame range, optionally following a tracked path (ADR-0016).

## Why
The unreviewed state must err towards redaction, because review is incomplete in practice. With opt-in (2) an
unfinished review exports unredacted faces; with opt-out the worst case of an unfinished review is over-redaction.
Track-level actions make review proportional to the number of objects, not detections: rejecting one false-positive
track removes it on every frame.

## Benefits
Export of an unreviewed project is already safe. Status is an immutable record change (`with`), so UI edits and
background readers share snapshots without extra locking. Approved vs pending is visible for audit.

## Costs
Unreviewed exports can be over-redacted. Track-level reject depends on the tracker: a track that wrongly merges two
objects would reject both.

## Risks
A reviewer rejects a whole track that contains a real face; a status change made during export is expected to apply.
The rule only protects faces the AI *found*: a frame the scan never reached has no boxes at all, and a model can miss a
face on a frame it did analyse.

## Mitigation
Rejected detections stay in the repository (reversible, shown under a "Rejected" filter); each list item shows its
track number, so a track can be inspected before it is rejected. Export works on a snapshot taken at start (ADR-0019),
so what is exported is the review state at the moment "Export" was pressed. Frames outside the scan are made visible
before export: the snapshot computes `RedactionCoverage` and Export warns when it is incomplete (ADR-0019, update
2026-10-11). Faces the model misses on analysed frames are covered only by manual regions and human review.

## When to reconsider
If an audit trail is required (who approved what, when), status changes become events with user and timestamp.
