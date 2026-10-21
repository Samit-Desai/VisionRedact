# Profiling

How performance questions in this codebase are answered, which tool answers which question, and what evidence
already exists. The rule (EngineeringGuidelines.md): **no optimization without a before/after measurement.**

## Evidence already in the repo

| Question | Instrument | Where |
|---|---|---|
| Is playback keeping up? Frames dropped? | Diagnostics panel (presented/decode FPS, late drops, UI-replaced frames) | app → Diagnostics tab |
| Is analysis bounded? | Diagnostics panel (channel occupancy, AI FPS, live native frames) | app → Diagnostics tab |
| Allocation per operation, GC pressure | `GC.GetAllocatedBytesForCurrentThread`, `GC.CollectionCount` | `tools/VisionRedact.Benchmarks` B2, B3 |
| Throughput, latency percentiles | Stopwatch, p50/p95 | Benchmarks B1, B3, B4, B6, B7 |
| Memory growth over time | private bytes sampled every 0.5 s; `OwnedVideoFrame.LiveInstances` | Benchmarks B5; integration soak test |
| Native frame leaks | `OwnedVideoFrame.LiveInstances` returns to baseline | integration tests, diagnostics panel |

The live-instance counter is the cheapest leak detector for native memory: if it climbs during steady playback, a
frame is not being disposed somewhere, regardless of what the GC thinks.

## Tool → question

| Tool | Use it for | What to look at |
|---|---|---|
| **Visual Studio Profiler — CPU Usage** | hot paths (why is export slow?) | hot path tree; for optimization 3 it points at `CvInvoke.GaussianBlur` under `GaussianBlurEffect.Apply` |
| **Visual Studio — .NET Object Allocation** | managed allocation sources | allocations by type: before optimization 2, `Single[]` (4.9 MB) from `LetterboxPreprocessor..ctor` dominates |
| **Visual Studio — Concurrency / Threads** | UI thread blocked? contention? | UI thread must show no long blocking segments while decoding/analyzing |
| **dotnet-counters** | live GC and allocation rate in a running app | `dotnet-counters monitor -n VisionRedactStudio System.Runtime` → *Allocation Rate*, *Gen 2 GC Count*, *GC Heap Size*, *ThreadPool Queue Length* |
| **dotnet-trace / PerfView** | GC pauses, thread-pool starvation, allocation stacks | `GCStats`, `Thread Time`; thread-pool injection events would reveal blocking work on the pool |
| **dotMemory** (or VS memory snapshots) | managed leaks and retention paths | compare two snapshots across a playback loop: no growth of `MatVideoFrame`/`Mat` instances |
| **VMMap** | native vs managed memory split | *Private Data* (native heaps: OpenCV frames, ONNX arenas) vs *Managed Heap*; a growing Private Data with a flat Managed Heap = native leak the GC can't see |
| **Windows Performance Recorder/Analyzer (WPR/WPA)** | whole-system: CPU per thread incl. native frames, disk I/O, GPU | native stacks inside `cvextern.dll`/`onnxruntime.dll`; disk read throughput on multi-GB files |
| **WPF Performance Suite / Snoop** | layout/render cost, visual count | the detection list must realize only visible rows (virtualization) |

## Recipes

### "Playback stutters"
1. Diagnostics: are *late frames dropped* rising (decode too slow) or *preview frames replaced* rising (UI thread too slow)?
2. Decode-bound → CPU profile the playback decode task; codec/resolution; consider hardware decode.
3. UI-bound → VS Concurrency view / WPA on the UI thread: layout storms, `WritePixels` cost, GC pauses (Gen2 count in dotnet-counters).

### "Memory keeps growing"
1. Diagnostics: *Live native frames* rising? → an ownership bug (a frame not disposed). Find it by reviewing the path
   that changed; tests assert every `FakeVideoDecoder` frame is disposed.
2. Live frames flat but *private bytes* rising → VMMap: native heap (OpenCV/ONNX) or managed? dotMemory for managed.
3. Neither → working set includes mapped files and arenas; compare after warm-up, not from process start (B5 does this).

### "Analysis is slow"
1. Channel occupancy **full** → inference-bound (expected); options: sampling, smaller model, GPU.
2. Channel occupancy **empty** → decode-bound: check `Skip` is used for unsampled frames (optimization 1).

## What was *not* captured

Screenshots from the Visual Studio Profiler, dotMemory or WPA are not stored in the repository (binary, machine
specific). The measurements above are reproducible with the benchmark harness; the recipes say which profiler view
confirms each one. Recording a WPR trace of an export before/after optimization 3 shows the change directly.
