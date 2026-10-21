# Threading Model

## Purpose

Guarantee three properties: the WPF Dispatcher is never blocked by media work, no native resource is used by two
threads at once, and every background failure ends in a known state instead of an unobserved exception.

## Threads and tasks, and what they own

```text
┌──────────────────────── UI thread (STA, Dispatcher) ─────────────────────────┐
│ Views, ViewModels, WriteableBitmap, bindings, commands                       │
│ DispatcherTimer 4 Hz: DetectionsViewModel.Flush (drain repository changes)   │
│ DispatcherTimer 2 Hz: PlayerViewModel.OnOverlayTick (repository.Version)     │
│ PeriodicTimer 2 Hz: DiagnosticsViewModel.SampleLoopAsync (while visible)     │
└──────▲─────────────────────────────────────────┬─────────────────────────────┘
       │ IUiThread.Post / presenter BeginInvoke   │ mailbox writes, job.Cancel(), ProcessingJobManager.Start
       │ (asynchronous, coalesced)               ▼
┌──────┴──────── PlaybackSession ─────────────────────────────────────────────┐
│ decode task:    only caller of decoder #1; skips frames it can't deliver    │
│ redaction task: RedactionPreviewProcessor, overlapping the next decode      │
│ present task:   PlaybackClock pacing, late-frame drops, IPreviewFrameSink   │
│     joined by Channel(1) and Channel<DecodedFrame>(3, Wait)                 │
└─────────────────────────────────────────────────────────────────────────────┘
┌──────────────── ProcessingJobManager: one Task.Run per job ─────────────────┐
│ AnalysisJob       producer task (decoder #2) ─Channel(4, Wait)─► consumer   │
│                   task (detector, tracker, repository)                      │
│ ExportJob         VideoExportService: decode ‖ redact ‖ encode (3 tasks)    │
│ RegionTrackingJob sequential decode + visual tracker (own decoder)          │
└─────────────────────────────────────────────────────────────────────────────┘
┌──────────────── Thread pool (Task.Run at use-case boundaries) ──────────────┐
│ VideoWorkspace.OpenAsync (blocking native open), runtime probe,             │
│ DetectorHost model load, FrameDetectionService (decode + detect one frame)  │
└─────────────────────────────────────────────────────────────────────────────┘
```

| Thread / task | Started by | Owns | Talks to the UI via |
|---|---|---|---|
| UI thread | WPF | all `DispatcherObject`s, `WriteableBitmap` | — |
| Playback decode task | `PlaybackSession.Start` (`Task.Run`) | decoder #1; frames until written to the one-frame channel | nothing (no UI access) |
| Playback redaction task | `PlaybackSession.Start` (`Task.Run`) | the frame being redacted (`IPreviewFrameProcessor`) until written to the decode-ahead channel | nothing (no UI access) |
| Playback present task | `PlaybackSession.Start` (`Task.Run`) | `PlaybackClock`, the held look-ahead frame | `IPreviewFrameSink.Present`; `StateChanged` (ViewModel posts) |
| Analysis producer | `AnalysisJob.ExecuteAsync` (`Task.Run`) | decoder #2 | throttled `ProgressChanged` (≤ 4/s) |
| Analysis consumer | `AnalysisJob.ExecuteAsync` (`Task.Run`) | the frame being analyzed; calls the detector and tracker | repository `Changed` → `ConcurrentQueue` → 4 Hz flush |
| Job runner | `ProcessingJobManager.Start` (`Task.Run(job.RunAsync)`) | the job's state machine and cancellation source | `StateChanged`, `ProgressChanged` → `IUiThread.Post` |
| Export pipeline | `VideoExportService.RunPipelineAsync`: decode, redact and encode tasks joined by two bounded channels (capacity 4) | decoder #3 (decode task only), `IVideoWriter` (encode task only; with FFmpeg, a child `ffmpeg.exe` process fed over stdin), ≤ ~11 frames in flight | via `ExportJob` progress |
| Region tracking | `RegionTrackingJob` (job runner) | its own decoder and `IVisualTracker` | via job progress; region store `Changed` → `Post` |
| Model load | `DetectorHost.GetAsync` (`Task.Run`, `SemaphoreSlim` gate) | the new `InferenceSession` until cached | awaited by the ViewModel |

## Rules

1. **Blocking native work never runs on the Dispatcher.** Opening a container, decoding, loading `cvextern.dll`,
   loading an ONNX model and running inference are blocking; they run on the playback tasks, a job, or via `Task.Run`
   at the use-case boundary (`VideoWorkspace.OpenAsync`, `ShellViewModel.InitializeAsync`, `DetectorHost.GetAsync`,
   `FrameDetectionService.DetectFrameAsync`, `VideoExportService.ExportAsync`).
2. **One owner per decoder.** `IVideoDecoder` is not thread-safe. Every pipeline opens its own; within
   `PlaybackSession` only the decode task calls it, and public methods only write to a mailbox. `FakeVideoDecoder`
   detects concurrent calls and `Decoder_is_never_called_concurrently_under_command_storms` hammers the API from 400
   parallel operations.
3. **Workers never touch WPF.** They hand frames to `IPreviewFrameSink`, raise events, or update stores; the WPF side
   marshals. There is exactly one presentation boundary for frames (`WriteableBitmapFramePresenter`) and one injectable
   boundary for ViewModels (`IUiThread`). No `Application.Current.Dispatcher.Invoke` in Application or Infrastructure.
4. **Post, never Invoke.** `IUiThread` offers only asynchronous `Post`. A synchronous `Invoke` from a worker blocks the
   worker on the UI thread — and deadlocks if the UI thread is simultaneously awaiting that worker (e.g. during
   shutdown).
5. **Dispatcher work is bounded and coalesced.** At most one render callback is queued at a time; each does one
   `WritePixels` memcpy. Detection changes are queued and drained at 4 Hz, overlays re-checked at 2 Hz, job progress
   throttled to one event per 250 ms. A 50,000-detection analysis costs a handful of UI updates per second, not 50,000
   Dispatcher operations.
6. **async all the way; no sync-over-async.** No `.Result`, `.Wait()`, or `GetAwaiter().GetResult()` in product code.
   Shutdown is asynchronous: the shell cancels the first `Closing`, awaits `ShutdownAsync`, then closes again.
7. **`async void` only for true event handlers** (`Loaded`, `Closing`) and the `ICommand.Execute` boundary
   (`AsyncCommand`), each with error handling.
8. **Library code uses `ConfigureAwait(false)`.** Application/Infrastructure never need the UI context; ViewModel
   code does not use it, so its continuations return to the Dispatcher.

## Task ≠ Thread ≠ async

- A **thread** is an OS scheduling unit with a stack. The UI thread is one specific thread with a message loop.
- A **Task** is a promise of a result. It may run on a pool thread, complete synchronously, or represent pure I/O
  with no thread at all while pending.
- **async/await** is a compiler rewrite into a state machine; `await` does not create a thread. The playback tasks
  are async loops, yet they occupy a thread only while decoding or presenting — while waiting in `WriteAsync`,
  `ReadAsync` or `SemaphoreSlim.WaitAsync` they hold none.
- **`Task.Run`** queues synchronous CPU/blocking work to the pool. It is used where that is precisely the need
  (blocking native open/decode/inference), not sprinkled to "make things async".

## How playback commands avoid blocking

```text
UI: session.Seek(900)
    CAS loop: _request = new PositionRequest(generation + 1, 900)   ← latest wins (coalescing)
    _producerWake.Release(); _consumerWake.Release()                ← returns in microseconds
Decode task (next loop turn):
    request.Generation != mine → decoder.Seek(900) → ReadNext → WriteAsync(DecodedFrame(gen, frame))
Present task:
    request.Generation != mine → read frames, dispose any from older generations → Present(frame 900)
```

Why a mailbox and not a command queue: scrubbing produces hundreds of seeks per second; a FIFO would make the video
lag further behind the mouse the longer you drag. Only the latest target matters, and the generation number lets the
present task throw away frames that were decoded for an older target. Play/pause is a single volatile flag; steps
accumulate (`Interlocked.Add`) because two quick "next frame" presses should move two frames.

## Waiting for a frame's due time

`SemaphoreSlim.WaitAsync(timeout, ct)` serves as both the timer and the wake-up signal: a command interrupts the wait
immediately, otherwise the timeout expires when the frame is due. It returns `bool` — no exception per frame.
If a command interrupts the wait, the frame is kept in `_held` (resume or step-forward wants exactly that frame); if
cancellation interrupts it, the frame is also parked in `_held` so `DisposeAsync` releases it — this closed a real
frame leak. The semaphores are intentionally never disposed (they own no kernel handle unless `AvailableWaitHandle` is
used), which removes a Release-after-Dispose race with UI-thread callers.

## Background jobs

`ProcessingJob` is the template for analysis, export and region tracking:

```text
ProcessingJobManager.Start(job) → Task.Run(job.RunAsync)
  Created ─► Running ─► Completed
                 │  └─► Failed      (exception, captured into Error — never unobserved)
                 └─► Cancelling ─► Cancelled   (only if *this job's* token was cancelled)
```

- State transitions are `Interlocked.CompareExchange` on an int, validated by `JobStateMachine`; `Cancel()` is safe from
  any thread, repeatedly, and before start.
- `Completion` is a `TaskCompletionSource` created with `RunContinuationsAsynchronously` and never faults, so awaiting
  it cannot throw and continuations never run inline on the job's thread.
- `ProcessingJobManager.CancelAllAsync` cancels every active job and awaits their completion. It runs when another video
  is opened (`VideoWorkspace.OpenAsync`) and at shutdown, *before* the detectors and decoders the jobs use are disposed.
- `AnalysisJob` runs two inner tasks joined by `Channel<OwnedVideoFrame>(4, Wait)` and a linked token: whichever side
  fails cancels the other; `Task.WhenAll` would surface the secondary `OperationCanceledException`, so the job
  re-throws the first non-cancellation exception with `ExceptionDispatchInfo`.

## Shutdown ordering

```text
ShellViewModel.ShutdownAsync
  1. ProcessingJobManager.CancelAllAsync()   jobs stop using decoders and detectors
  2. VideoWorkspace.DisposeAsync()           → PlaybackSession.DisposeAsync:
        cancel lifetime token + wake both tasks
        await decode task and present task   (decode task may be inside a native call; we wait asynchronously)
        dispose held + buffered frames
        dispose decoder                      only now: no thread can still be using it
  3. DetectorHost.Dispose()                  unload ONNX sessions (native memory)
```

Disposing a decoder or an `InferenceSession` before its user has exited would be a use-after-free inside native code.

## Could two threads mutate this state?

| State | Writers | Mechanism |
|---|---|---|
| playback mailbox (`_request`, `_pendingStep`, `_playRequested`) | any thread | CAS on an immutable `PositionRequest`; `Interlocked.Add`; volatile |
| playback `_state`, `_currentFrame`, statistics | playback tasks | Interlocked, read by any thread |
| held frame, generation, clock, drop counter | present task only | none needed |
| decoder position | decode task only | none needed |
| presenter slot `_pending` | present task writes, UI takes | `Interlocked.Exchange` |
| presenter `_bitmap` | UI only | thread affinity |
| `InMemoryDetectionRepository` | analysis consumer, "detect frame", UI review | one lock, never held while raising events; immutable records |
| `RedactionRegionStore` | UI, `RegionTrackingJob` | copy-on-write array; readers (playback redaction task, export snapshot) take the reference without locking |
| `OnnxObjectDetector` buffers | whichever job calls `Detect` | `lock` (reused preprocessing buffers are not thread-safe) |
| `DetectorHost` cache | any caller | `SemaphoreSlim(1, 1)` around load |
| job state | job thread, any caller of `Cancel` | CAS validated by `JobStateMachine` |

## Debugging note: a second Dispatcher under the Visual Studio XAML tools

Symptom, seen **only** when the app runs under the Visual Studio debugger (F5): an endless stream of
`ArgumentException: This API was accessed with arguments from the wrong context`, usually during layout.
`Ctrl+F5` never showed it; it reproduced on .NET 8 and .NET 10.

Diagnosis with `DispatcherAffinityProbe` (logged once, on the first such error): all 185 visuals in the window
belonged to the window's Dispatcher, yet `Dispatcher.CurrentDispatcher` on that same UI thread was a *second*
Dispatcher instance (`#0037ADF8` vs `#017ADAB1`), and `Microsoft.VisualStudio.DesignTools.WpfTap` was loaded. New WPF
objects take `Dispatcher.CurrentDispatcher`, so every freshly created element (item containers, clip geometries) was
"from the wrong context" relative to the tree it was inserted into. Not an application threading bug.

Fix: Visual Studio *Tools > Options > Debugging > General* → untick **Enable UI Debugging Tools for XAML** (and XAML
Hot Reload for WPF). The global handler detects the condition (`Dispatcher.CurrentDispatcher != Dispatcher`) and says
so in its dialog. See [ErrorHandling.md](ErrorHandling.md#visual-studio-xaml-tools-and-a-second-dispatcher).

## Testing

`PlaybackSessionTests` covers command responsiveness while the decoder is blocked, coalescing, the concurrency storm,
disposal during playback, and worker faults; `DecodeAheadTests` covers the bounded buffer, seek flushing and the
preview processor on the redaction task. `PlaybackClockTests` uses `FakeTimeProvider` for deterministic timing.
`ProcessingJobTests` covers every job transition, foreign-token cancellation, `CancelAllAsync` and progress throttling;
`AnalysisJobTests` covers backpressure, cancellation of both tasks and root-cause failure;
`InMemoryDetectionRepositoryTests` and `RedactionPlannerTests` include concurrent writer/reader tests.
