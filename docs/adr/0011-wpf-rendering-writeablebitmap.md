# ADR-0011: WPF Rendering via One WriteableBitmap and Coalesced Dispatch

## Context
Decoded BGR frames must appear in WPF at up to 60 FPS with minimal UI-thread cost; overlays (detections, redaction
rectangles) will be layered on top.

## Options
1. Create a new `BitmapSource` per frame (`BitmapSource.Create`, frozen).
2. One `WriteableBitmap`, `WritePixels` per frame.
3. `D3DImage` / DirectX interop.
4. `MediaElement`.

## Decision
Option 2: `WriteableBitmapFramePresenter` owns one `WriteableBitmap` (Bgr24, 96 DPI), recreated only on size change.
The worker swaps frames into a single-slot mailbox; at most one `BeginInvoke(Render)` is queued; the callback copies
pixels and disposes the frame.

## Why
(1) allocates ~6 MB managed per frame (LOH churn → GC pauses) and rebinds `Image.Source` every frame. (2) is a single
memcpy from native memory into the back buffer with no allocation and no layout invalidation. (3) is the fastest path
but adds DirectX interop complexity not justified until profiling shows (2) is the bottleneck. (4) gives no pixel
access.

## Benefits
Allocation-free steady state; minimal Dispatcher work; overlays stay separate WPF layers (no burning boxes into pixels).

## Costs
The copy still happens on the UI thread; Bgr24 may be converted internally by WPF to Pbgra32 when composing.

## Risks
UI-thread copy cost at 4K/60 (≈1.5 GB/s).

## Mitigation
Measure in slice 2 diagnostics; candidates: Bgr32/Pbgra32 conversion on the worker, `Lock`/`AddDirtyRect` with a
background copy into the back buffer, or D3DImage.

## When to reconsider
When profiling shows `WritePixels` or composition dominating UI-thread time.
