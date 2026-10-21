# ADR-0008: Explicit Native Frame Ownership

## Context
Decoded frames are ~6 MB (1080p) to ~25 MB (4K) of native memory behind tiny managed objects. The GC does not see
native memory, so it will not collect leaked frames in time.

## Options
1. Rely on finalizers.
2. Share frames with reference counting (`AddRef`/`Release`).
3. Copy pixels into managed arrays (`byte[]`) at the boundary.
4. Single-owner frames with explicit transfer and `Dispose`.

## Decision
Option 4: `OwnedVideoFrame` (abstract, Application) with `MatVideoFrame` (Emgu) as the production implementation.
One owner at a time; transfer or dispose in `finally`; idempotent, race-safe `Dispose`; live-instance counter.

## Why
(1) lets native memory balloon between GCs. (2) works but spreads lifetime reasoning across every consumer and
makes leaks hard to attribute. (3) doubles memory traffic and puts 6 MB arrays on the LOH, creating GC pauses during
playback. Single ownership is simple to reason about and to test.

## Benefits
Bounded, predictable memory; zero-copy access for WPF and (later) AI; leaks are countable (`LiveInstances`).

## Costs
Every code path must honour the contract; fan-out requires explicit copies.

## Risks
A missed `finally` leaks; use-after-dispose reads freed memory.

## Mitigation
`DataPointer` throws after dispose; span-only access (no storable buffers); tests assert every created frame is
disposed; integration tests check the live count returns to baseline.

## When to reconsider
If fan-out becomes common (e.g. one decode feeding export and analysis), add an explicit `Clone`/ref-counted variant
rather than weakening single ownership.
