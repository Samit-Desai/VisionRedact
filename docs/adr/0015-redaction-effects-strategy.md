# ADR-0015: Redaction Effects as Strategies, One Shared Planner

## Context
Users need different effects: blur (keeps context), pixelate (clearly marked as redacted) and solid black (removes all
information). The same redaction must appear in the preview while reviewing and in the exported file. Effects are
native pixel operations (Emgu CV); the decision of *where* to redact is domain logic that must be testable without
native DLLs.

## Options
1. A `switch` on the effect kind inside preview and export code.
2. Effects as strategy objects behind an interface; a pure planner decides regions; preview and export call the same
   planner.
3. Separate preview (fast approximation, e.g. WPF shader) and export (exact) implementations.

## Decision
Option 2. [`IRedactionEffect`](../../src/VisionRedact.Application/Redaction/RedactionContracts.cs) has one
implementation per `RedactionEffectKind` in
[`RedactionEffects.cs`](../../src/VisionRedact.Infrastructure.Video/RedactionEffects.cs): `GaussianBlurEffect`,
`PixelateEffect`, `SolidBlackEffect`. `RedactionProcessor` dispatches by kind and re-clamps each ROI.
[`RedactionPlanner.Plan`](../../src/VisionRedact.Application/Redaction/RedactionPlanner.cs) is a pure static function:
non-rejected detections, inflated by `RedactionSettings.Padding` (default 0.2 of the box size per side), plus manual
regions active on the frame, converted to clamped pixel ROIs. Both
[`RedactionPreviewProcessor`](../../src/VisionRedact.Application/Redaction/RedactionPreviewProcessor.cs) (playback
decode thread) and [`RedactionSnapshot.PlanFor`](../../src/VisionRedact.Application/Export/RedactionSnapshot.cs)
(export) call it with `GetForDisplay` detections. Blur and pixel-block sizes scale with the region, so a 30 px and a
400 px face are equally unrecognizable. Large regions (short side > 64 px) are blurred at reduced resolution: downscale
to 48 px with area averaging, blur with a proportional kernel, upscale.

## Why
A shared planner and shared effects make "what you reviewed is what you export" a structural property instead of two
implementations that must be kept in sync (3). Strategies keep native code out of the Application layer and let a new
effect be added without touching the planner or pipelines. Padding exists because model boxes hug the face; hair,
ears and motion blur extend beyond them.

## Benefits
Planner rules (rejected skipped, pending redacted, padding, clamping, tracked region paths) are unit-tested without
Emgu. Reduced-resolution blur: 61.9 → 1.5 ms per 576×324 region (41×); 1080p MP4 export 13 → 75 frames/s (5.7×).

## Costs
Preview pays the real effect cost on the decode thread (proportional to redacted area). Reduced-resolution blur is not
bit-identical to a direct Gaussian.

## Risks
A blur that is too weak leaves a recognizable face; an effect bug writes outside the ROI.

## Mitigation
Two blur passes at strength ≥ 0.5; tests assert that fine detail inside the region is destroyed and nothing outside
changes (on the direct path for small regions; the reduced-resolution path is not yet covered by such a test). Solid black is available when no residual information is acceptable.

## When to reconsider
If preview cost becomes visible at 4K with many regions, move effects to the GPU — still behind `IRedactionEffect`.
