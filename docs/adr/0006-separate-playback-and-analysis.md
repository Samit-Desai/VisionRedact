# ADR-0006: Separate Playback and Analysis Pipelines

## Context
Users keep reviewing video (play, scrub, step) while AI analysis runs over the whole file in the background.

## Options
1. One decoder; analysis consumes the frames playback decodes.
2. One decoder with a fan-out broadcaster; each consumer applies its own policy.
3. Independent decoders per pipeline.

## Decision
Option 3. `PlaybackSession` owns decoder #1; the analysis job (slice 4) opens decoder #2 via `IVideoDecoderFactory`.

## Why
Playback seeks constantly and drops late frames; analysis walks sequentially and must not lose frames by accident.
Sharing (1) means every scrub scrambles analysis, or analysis stalls playback. A broadcaster (2) couples both to the
slower consumer and makes frame lifetime shared (reference counting) — exactly the ownership complexity ADR-0008
avoids.

## Benefits
Each pipeline has its own position, policy and failure mode; UI responsiveness is independent of inference speed;
single-owner frames.

## Costs
The file is decoded twice while both run: more CPU and disk read.

## Risks
Decode contention slows both on weak machines.

## Mitigation
Analysis uses sampling and a bounded channel; diagnostics expose decode/AI FPS; analysis priority can be lowered.

## When to reconsider
If profiling shows decode (not inference) dominates analysis cost, consider sharing a sequential decode for analysis
and export, never for playback.
