# ADR-0005: Bounded Channels Between Pipeline Stages

## Context
Analysis decodes frames (fast, ~30+ FPS) and runs inference (slow, e.g. 8 FPS on CPU). Stages run concurrently and
exchange ~6 MB native frames.

## Options
1. `BlockingCollection<T>` — blocks threads; no async.
2. `ConcurrentQueue<T>` + polling/semaphores — unbounded unless hand-bounded; easy to get wrong.
3. TPL Dataflow — capable, heavier, `BoundedCapacity` per block; more concepts than needed.
4. `System.Threading.Channels`, **bounded**, `FullMode = Wait`.
5. Unbounded channel.

## Decision
Bounded `Channel<OwnedVideoFrame>` with `FullMode.Wait`, small configurable capacity (starting value 4), single
producer (the decoder is not thread-safe), 1..N consumers.

## Why
With 30 FPS in and 8 FPS out an unbounded queue grows by 22 frames/s — ≈130 MB/s at 1080p — until the process dies.
A bounded channel turns that into backpressure: `WriteAsync` waits asynchronously (no blocked thread) and the decoder
runs at the consumer's pace. Memory becomes `capacity × frame size`, independent of video length.
`DropOldest`/`DropWrite` are wrong for analysis: silently discarding frames under load would make coverage depend on
machine speed. Skipping frames is the job of `IFrameSamplingStrategy`, explicitly.

## Benefits
Bounded memory; async backpressure; built into .NET; simple completion/fault propagation (`Writer.Complete(ex)`).

## Costs
Throughput limited by the slowest stage (by design); capacity must be tuned.

## Risks
Frames left in the channel on cancellation/failure leak native memory; deadlock if the producer awaits something the
consumer holds.

## Mitigation
`finally` drains and disposes remaining items; consumers never call back into the producer; tests for producer faster
than consumer, cancellation mid-stream, consumer exception.

## When to reconsider
If stages multiply (decode → preprocess → infer → track), TPL Dataflow's linking may read better. Capacities are
revisited with profiling data, not guessed.
