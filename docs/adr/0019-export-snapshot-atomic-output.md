# ADR-0019: Export From a Snapshot, Write via a Temporary File

> **Update (2026-09):** "sequential on one thread" and "no audio" below are superseded: export runs as three
> overlapped stages (optimization 6), and the FFmpeg writer carries the audio ([ADR-0020](0020-ffmpeg-exporter.md)).
> Snapshot, temp file, frame-count check and the final rename are unchanged. Measured now: 147–167 frames/s (FFmpeg).

## Context
Export of a long 1080p file runs for minutes (measured 75 frames/s for MP4 with one blurred region) while the reviewer keeps approving, rejecting and
drawing regions, or opens another video. The output is an evidence copy: it must contain every frame, reflect one
well-defined review state, and never exist in a half-written form under its final name. The source must stay
untouched. ADR-0007 fixed the pipeline shape; this ADR records how its input and output are made safe.

## Options
1. Read the live detection repository and region store while encoding.
2. Lock review for the duration of the export.
3. Capture an immutable snapshot at start; export from it with its own decoder.

For output:
a. Write directly to the requested file.
b. Write to a temporary file next to it, verify, then move it into place.

## Decision
Options 3 and b. [`RedactionSnapshot.Capture`](../../src/VisionRedact.Application/Export/RedactionSnapshot.cs) copies
detections (with analyzed frames and `HoldFrames`) into a private `InMemoryDetectionRepository`, plus the regions and
`RedactionSettings`; `PlanFor` calls the same `RedactionPlanner` and `GetForDisplay` as preview.
[`VideoExportService`](../../src/VisionRedact.Application/Export/VideoExportService.cs) opens its own decoder, reads
sequentially from frame 0 (never seeks, never skips, never drops), applies the plan to its own decoded copy, and writes
to `<name>.partial-<guid><ext>` in the target folder. After the loop it requires `IVideoWriter.FramesWritten` to equal
the frames read (and more than zero), disposes writer and decoder, then `File.Move(temp, output, overwrite: true)`.
Failure or cancellation deletes the temp file. An output path equal to the source is rejected up front
(`ExportErrorKind.SameAsSource`).

## Why
Reading live state (1) makes the result depend on when each frame happened to be encoded; opening another video resets
the live stores mid-export. Locking review (2) blocks the user for minutes. A snapshot makes the export deterministic:
it corresponds exactly to the review state when "Export" was pressed. The temp-file move means the target name only
ever holds a complete, verified export; a crash leaves at most a `.partial` file, never a truncated file under the
target name.

## Benefits
The user keeps working during export. Tests cover every frame in order, redactions applied to exactly the right frames,
snapshot isolation from later review changes, no partial file on cancellation or writer failure, an encoder that
drops frames reported as `IncompleteOutput`, and the source never being the output; an integration test checks the
source is untouched after a real MP4/AVI export.

## Costs
The snapshot copies all detections (50,000 detections snapshot in 6 ms). Sequential decode → redact → encode on one
thread limits throughput; temporary disk space for a second copy is needed until the move. No audio: OpenCV's
`VideoWriter` writes video only ([`EmguVideoWriter`](../../src/VisionRedact.Infrastructure.Video/EmguVideoWriter.cs);
codec fallback avc1 → mp4v for MP4, MJPG for AVI).

## Risks
A frame-count match does not prove every frame is correct; the move can fail across volumes or when the target is
locked; a snapshot taken before analysis finishes omits later detections.

## Mitigation
The temp file is created in the target folder, so the move is a rename on the same volume. Access errors surface as
`ExportErrorKind.AccessDenied`. Redaction correctness is covered by planner and export tests on known frames. Export
reports the codec actually used and the redacted-frame count.

**Update 2026-10-11 — two guarantees made explicit.**
- *Coverage:* frames the AI scan never reached (no scan, a cancelled scan, or no sample's hold window) have no boxes,
  so only manual regions apply there. The snapshot now computes `RedactionCoverage` with the same rule as
  `GetForDisplay`; when it is incomplete, Export asks first (default: No) and lists the unscanned time ranges, and
  the completion dialog repeats the coverage. A stricter policy (block export below 100 %) is a possible next step for
  regulated workflows.
- *Source completeness:* OpenCV reports a damaged packet exactly like the end of the file, so a truncated or corrupt
  source used to export as a silently shorter video that still passed the frame-count check. The export now compares
  the decoded frames with the container's frame count (tolerance: 3 frames or 0.5 %, because some containers only
  estimate it) and reports `SourceEndedEarly`; the UI shows a warning. The file itself stays valid.

**What these checks do and do not guarantee.**

| Check | It shows | It does not show |
|---|---|---|
| Coverage complete | every frame was analysed, or is held from a sample at most `HoldFrames` earlier | that every face was found: a model can miss a face on a frame it analysed |
| Encoded = decoded frames | the encoder wrote every frame it was given | that the decoder read the whole source |
| Decoded ≈ container frame count | no large part of the source is missing | that nothing is missing: the count is an estimate for some formats, and a loss inside the tolerance passes |

The completeness check is evidence, not proof. OpenCV gives no decode-error codes, and the app does not read per-frame timestamps (CAP_PROP_PTS exists, unused) here
(frame time is index ÷ fps), so a stronger check is not possible behind the current decoder. The real fix is an
FFmpeg-based decoder behind `IVideoDecoderFactory` (ADR-0018 "When to reconsider"; docs/Tradeoffs.md): decode errors
reported as errors, real timestamps (also needed for variable-frame-rate sources), and a check of the last timestamp
against the stream duration. "No face leaks" would
additionally need a ground-truth evaluation of the detector's misses, which does not exist yet.

## When to reconsider
When audio or exact timestamps are required: an FFmpeg adapter behind `IVideoWriter` that remuxes the original audio
(ADR-0018). If throughput matters more, a bounded decode ‖ encode pipeline — measured first.
