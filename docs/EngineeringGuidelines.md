# Engineering Guidelines

Written for engineers working on this codebase. The rules exist because each one prevents a class of bug that is
expensive to find in a media application: frozen UIs, native memory growth, and crashes that only happen at minute 47
of a 3-hour file.

## Pull requests

- One concern per PR; keep the build green (`dotnet build`, `dotnet test`, zero warnings — warnings are errors).
- Describe **what could block the UI, grow without bound, or leak native memory** in the change, even if the answer is
  "nothing".
- Performance claims include before/after measurements and the tool used. "Should be faster" is not evidence.
- Behaviour changes come with tests; bug fixes come with a test that failed before the fix.
- Update the relevant doc/ADR in the same PR when a design decision changes.

## Architecture boundaries

- Domain references nothing. Application references Domain + logging abstractions only.
- No Emgu CV, ONNX Runtime, WPF, Prism, `Dispatcher` or `WriteableBitmap` types in Domain/Application —
  if you need one, you need a port.
- Every new interface must name the second implementation or the test that needs it.
- Infrastructure projects do not reference each other.

## Async and threading

- async all the way. No `.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Thread.Sleep` in product code.
- `async void` only for event handlers and `ICommand.Execute` (use `AsyncCommand`), always with a catch.
- `Task.Run` only to move synchronous blocking/CPU work off the UI thread, at the use-case boundary. Not inside
  already-async code, not "just in case".
- Library code (Application/Infrastructure) uses `ConfigureAwait(false)`; ViewModels don't.
- Background code never touches WPF objects. Marshal through `IUiThread.Post` or a presenter adapter — never
  `Application.Current.Dispatcher.Invoke`.
- Anything that is not thread-safe (decoders, inference sessions if configured so) has exactly one owning loop.

## Cancellation

- Every long operation takes a `CancellationToken` as its last parameter and passes it on.
- Check the token between units of work (frames), not inside tight native calls you cannot interrupt; document the
  worst-case cancellation latency.
- Filter your own cancellation: `catch (OperationCanceledException) when (token.IsCancellationRequested)`.
- Whoever creates a `CancellationTokenSource` disposes it.
- If cancellation happens after acquiring a resource, release it (see `VideoWorkspace.OpenAsync`).

## Native ownership

- Every `Mat`, `VideoCapture`, `VideoWriter`, `InferenceSession`, `OrtValue` has one documented owner.
- Transfer ownership explicitly (comment `// ownership transferred`) and null the local so `finally` doesn't dispose it.
- Never queue a buffer the producer reuses (`QueryFrame`).
- Queues between stages are **bounded**. Justify the capacity and the full-mode.
- Never hold a `Span` over native memory beyond the owner's lifetime.

## WPF ViewModels

- ViewModels coordinate; they do not decode, infer, write files or compute redaction geometry.
- No per-frame `PropertyChanged` storms: frame-rate data goes through a presenter or is throttled/coalesced.
- EventAggregator for coarse events only.
- Views may contain code-behind for pure view glue (focus, drag gestures); no business logic.
- Lists that can grow large use virtualization (`VirtualizingStackPanel`, `VirtualizationMode="Recycling"`).

## Testing expectations

- Application logic is testable without WPF or native DLLs; if it isn't, the boundary is wrong.
- No `Thread.Sleep`/`Task.Delay` to "wait for" results; await a condition with a timeout.
- Integration tests for every adapter crossing the native boundary.

## Error handling

- Translate low-level exceptions in Infrastructure; keep the original as `InnerException`.
- Background failures become a terminal state + event, never an unobserved exception.
- Log with structured parameters (`{Path}`, `{Frame}`); never log only `ex.Message`.

## Documentation

Major subsystems document purpose, design, alternatives, trade-offs, threading, ownership, cancellation, failure
behaviour, testing and extension points. Significant decisions get an ADR. Don't document trivial folders.

---

## Code review example (fictional)

A PR adds "Detect faces" to the main ViewModel:

```csharp
public class MainWindowViewModel : BindableBase
{
    private readonly Queue<Mat> _frames = new();

    public DelegateCommand DetectCommand => new(async () =>
    {
        var capture = new VideoCapture(_path);
        var frame = new Mat();
        while (capture.Read(frame))
        {
            _frames.Enqueue(frame);                                   // (4) (5)
            Task.Run(() => ProcessNext());                           // (5)
        }
    });

    private void ProcessNext()
    {
        var frame = _frames.Dequeue();
        var faces = _detector.DetectAsync(frame).Result;            // (1)
        foreach (var face in faces)
        {
            if (face.Confidence > 0.6 && face.Width * face.Height > 400) // (7)
                Application.Current.Dispatcher.Invoke(() => Detections.Add(face)); // (6)
        }
    }
}
```

Review comments, in the tone I'd use:

1. **`.Result` on `DetectAsync`** — this blocks a pool thread per frame and, if `DetectAsync` ever captures the UI
   context, deadlocks. Could we make `ProcessNext` async and await it? Better still, see (5).
2. **UI-thread blocking** — the `while (capture.Read(...))` loop runs inside the command on the Dispatcher until the
   first `await`… and there is no `await`, so the entire file is decoded on the UI thread. The window will freeze for
   the length of the video.
3. **No cancellation** — a 2-hour video can't be stopped. Please accept a `CancellationToken`, expose Cancel, and
   check it per frame.
4. **Mat lifetime** — the same `frame` instance is enqueued every iteration and `Read` overwrites it, so every queued
   entry points at the latest pixels (and none are ever disposed). Each frame needs its own `Mat`, owned by whoever
   dequeues it and disposed in a `finally`. `capture` is never disposed either.
5. **Unbounded queue + unbounded `Task.Run`** — decode is ~30 FPS, detection maybe 8 FPS, so `_frames` grows by
   ~22 frames/s (~130 MB/s at 1080p) and we start one task per frame. Also `Queue<T>` isn't thread-safe. Let's use a
   bounded `Channel<OwnedVideoFrame>` with `FullMode.Wait` and a fixed number of consumers — that gives us
   backpressure for free.
6. **Dispatcher call per detection** — `Invoke` blocks the worker on the UI thread for every face; with thousands of
   detections the UI queue floods. Batch results and post once per interval, and use `BeginInvoke`/`IUiThread.Post`.
7. **Business rules in the ViewModel** — confidence and minimum-size thresholds are detection policy. They belong in
   the Application pipeline (configurable), where they can be unit-tested without WPF.

Suggested shape: `IAnalysisJob` in Application (decoder → sampler → bounded channel → detector → filter/NMS →
repository), started by a thin ViewModel command, reporting progress through `IProgress<T>` and completion through a
coarse event. The ViewModel ends up ~20 lines and the pipeline gets real tests with `FakeVideoDecoder` and
`FakeObjectDetector`.

None of these are personal failings — they are the exact traps this domain sets. The checklist above exists so we
catch them in review rather than in a customer's 3-hour body-cam file.
