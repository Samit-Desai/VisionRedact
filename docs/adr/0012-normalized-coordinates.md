# ADR-0012: Normalized Coordinates in the Domain

## Context
The same box is seen in at least three coordinate systems: the source frame (e.g. 1920×1080 pixels), the model input
(a 1080p frame letterboxed into 320×240 or 640×640), and the WPF video surface (resizable, measured in DIPs, any DPI).
Boxes are stored, persisted in projects, interpolated, tracked and finally turned into pixel writes.

## Options
1. Store source pixels (`int` rectangles) in the domain.
2. Store whatever the producer had (model pixels for AI, DIPs for mouse-drawn regions) and convert on use.
3. Store rectangles normalized to the source frame (0..1), convert only at the edges.

## Decision
Option 3. [`NormalizedRect`](../../src/VisionRedact.Domain/Geometry/NormalizedRect.cs) is the only box type in the
domain (`Detection.Box`, `RedactionRegion.Box`, `TrackPoint.Box`). Every constructor clamps to [0,1] with
non-negative size; NaN or infinite input becomes empty. One
[`LetterboxTransform`](../../src/VisionRedact.Domain/Geometry/LetterboxTransform.cs) (uniform scale + centred offset)
is used by both AI preprocessing ([`LetterboxPreprocessor`](../../src/VisionRedact.Infrastructure.AI/LetterboxPreprocessor.cs)
and the output decoders map model boxes back with `ContainerToNormalized`) and the WPF overlay
([`BoxOverlay`](../../src/VisionRedact.Presentation.Wpf/Controls/BoxOverlay.cs) draws with `NormalizedToContainer`;
[`VideoSurface`](../../src/VisionRedact.Presentation.Wpf/Controls/VideoSurface.cs) maps mouse input back). The only
conversion to pixels is `NormalizedRect.ToPixels`, which rounds outward (floor left/top, ceiling right/bottom, with a
1e-9 epsilon against floating-point noise) and clamps to the frame.

## Why
A detection is a fact about the source frame, not about a view of it. Normalized coordinates survive window resizes,
DPI changes, model input sizes and a different export resolution without conversion bugs. Letterbox math appears in
two places (model input, `Stretch=Uniform` display); sharing one implementation keeps overlay and inference aligned.
Outward rounding is a privacy decision: covering one extra pixel is harmless, missing one is a leak.

## Benefits
Clamping in the constructor means raw model output (negative, inverted, out-of-bounds) can never reach native pixel
code. `IntersectionOverUnion`, `Lerp`, `Inflate` and `Offset` work in one space for NMS, tracking, interpolation and
padding. Project files store resolution-independent boxes.

## Costs
`double` arithmetic and conversions at every edge; aspect ratio is implicit (a normalized square is not a pixel
square), so size comparisons must use the frame size where it matters.

## Risks
Rounding differences between preview and export; a conversion done by hand somewhere that skips clamping.

## Mitigation
One `ToPixels` used by [`RedactionPlanner`](../../src/VisionRedact.Application/Redaction/RedactionPlanner.cs) for
both preview and export; `RedactionProcessor` re-clamps before native writes. Domain tests cover clamping, NaN and
infinite input, outward rounding, letterbox-bar clipping and an exact display round trip for any container size.

## When to reconsider
If rotated or polygonal regions are needed, the representation grows (normalized polygon), but the principle holds.
