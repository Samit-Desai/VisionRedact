# Video Playback Pipeline

## Purpose

Low-latency, smooth preview of arbitrarily large files with instant response to play / pause / seek / step, without
ever decoding on the Dispatcher — and with the reviewer's redactions visible while playing.

## Architecture

```text
PlaybackSession (Application)
  mailbox (any thread): PositionRequest(generation, frame) · pending steps · play flag
        │                                        │
        ▼                                        ▼
  decode task                              present task
  sole caller of IVideoDecoder             generation filter (dispose stale frames)
  new generation → Seek                    PlaybackClock pacing
  ReadNext                                 late-frame drops (counted)
  RedactionPreviewProcessor                interruptible due-time wait
        │                                        │
        └──► Channel<DecodedFrame>(3, Wait) ─────┘
                                                 │ IPreviewFrameSink.Present (ownership moves)
                                                 ▼
WPF (Presentation)   WriteableBitmapFramePresenter: single slot (stale → dispose), one queued BeginInvoke,
                     WritePixels → Dispose → FrameRendered(index) → PlayerViewModel position, overlays, timeline
```

`PlaybackSession.Start` takes ownership of the decoder and starts both loops with `Task.Run`; they are async loops,
so they occupy a pool thread only while decoding or presenting.

## Decode-ahead buffer

- `Channel<DecodedFrame>` bounded to `PlaybackOptions.DecodeAheadFrames` (default **3**), `FullMode = Wait`,
  single reader / single writer. It absorbs decode-time jitter (slow keyframes, 4K) so presentation stays steady.
- Backpressure, not loss: when the buffer is full the decode task waits in `WriteAsync`. While paused it fills the
  buffer and then waits there, so step-forward is usually served from the buffer without a seek.
- The bound caps memory (3 × 6 MB at 1080p) and the latency a seek has to flush.
- A `DecodedFrame` carries the generation it was decoded for; a `null` frame marks end of stream for that generation.
- Measured on the 1080p59.94 AVCHD file: 59.9 decode FPS, 61.9 presented FPS, 3.8 ms average decode, 0 late drops,
  4 live frames (diagnostics panel).

## Seeking: a mailbox with generations

- `Seek`, `Refresh` and step-backward publish a new immutable `PositionRequest(generation + 1, startFrame)` with a
  compare-and-swap loop — lock-free, latest wins — and wake both tasks.
- The **decode task** sees a new generation, calls `decoder.Seek(startFrame)` (only if the decoder is not already
  there) and continues decoding from it.
- The **present task** sees a new generation and reads from the channel, disposing every frame from an older
  generation (`FramesDiscarded`, shown as "Discarded by seek"). A frame from a newer generation than it has processed is
  parked in `_held` until the loop catches up.
- Result: the buffer is flushed and a burst of seeks (scrubbing) only ever decodes the latest target.
- Scrubbing: dragging the timeline thumb pauses the session, seeks on every value change (coalesced), and resumes on
  release if it was playing. While dragging, rendered-frame notifications do not move the thumb.
- Step forward uses the next buffered frame when it is exactly current + 1 (no seek, no re-decode); step backward is a
  seek; steps accumulate (`Interlocked.Add`).
- `Refresh()` re-decodes and re-presents the current frame while paused — used after the reviewer edits a region, since
  buffered frames were processed with the previous redaction state.

### Seek accuracy (measured)

OpenCV's FFmpeg backend (`CAP_PROP_POS_FRAMES`) seeks to the preceding keyframe and decodes forward. Cost grows with
GOP length. Accuracy was measured, not assumed:

- Synthetic intra-only MJPEG (integration tests): frame-exact, verified by pixel content.
- The real 1080p59.94 AVCHD `.mts` (MPEG-TS, H.264): **5 of 5 seeks** matched sequential decode of the same frames,
  pixel difference **0.00**.
- After seeks in MPEG-TS, FFmpeg logs `missing reference picture` warnings while it decodes forward from the keyframe.
  They are harmless: the frame delivered at the target is still exact (above). They are logged by native FFmpeg, not
  by the app.

## Timing

`PlaybackClock` anchors at `(timestamp, frame)` on the first frame actually available after play starts or after a
seek — so decode latency after a seek is not counted as lateness. Frame *n* is due at
`anchor + (n − anchorFrame) / fps`, measured with `TimeProvider` (Stopwatch-based, monotonic).

Why not `await Task.Delay(33)`: each loop adds decode time + scheduling latency + timer granularity (~15.6 ms on
Windows). Delays accumulate, so a "30 FPS" loop plays slow and drifts from real time. Anchoring converts that error
into bounded jitter.

The wait is `SemaphoreSlim.WaitAsync(timeUntilDue, ct)`: it doubles as the wake-up for commands, so Pause/Seek/Step
interrupt it immediately. Waits under 1 ms are skipped. Known limitation: the default Windows timer means individual
frames can be up to ~15 ms late, and WPF composes at display refresh anyway. A high-resolution waitable timer is a
candidate in [Performance.md](Performance.md#known-limits-and-candidates-not-yet-done--no-claims), to be decided by
measuring presentation jitter.

## Frame dropping — preview only

Three places, all deliberate and counted. A frame that cannot be on screen in time is left out in the cheapest place
first:

1. **Decode task skips frames early** — before decoding a frame, the producer predicts when it would be ready (now +
   the smoothed cost of a frame decoded and redacted in full) against its due time, from the consumer's clock anchor
   (published as an immutable `PacingAnchor`, tagged with the seek generation). If it would be more than one frame
   late, the consumer would drop it anyway, so the producer calls `IVideoDecoder.Skip()` instead (Grab: decoded, as
   later frames depend on it, but no colour conversion, no copy, no redaction). Counted in `FramesDroppedLate` and
   `FramesSkippedEarly`. Never the first frame after a seek or the last frame; only while playing.
2. **Present task drops late frames** — if a frame's whole display slot has already passed (`PlaybackClock.IsLate`:
   more than one frame duration overdue), it is disposed instead of presented (`FramesDroppedLate`). Rules:
   - at most `MaxConsecutiveLateDrops` (default **4**) frames left out in a row, counting both skipped and dropped
     frames since the one on screen; then one is shown anyway and **the clock restarts from it**, so lateness can never
     pile up;
   - **never the last frame** — playback must visibly end on it (a real bug: a late last frame was dropped, so
     playback ended without showing it);
   - never while the clock is stopped (paused, stepping, seeking).
   - Skipped frames leave gaps in the buffer, so Step +1 and resume-after-pause check they got exactly the next frame
     and seek to it otherwise.
3. **Presenter replaces stale frames** — if the UI thread hasn't shown the previous frame when a new one arrives,
   the old one is disposed (`FramesDropped`, "Preview frames replaced").

Why the early skip exists (measured 2026-10-11, 4K60 with preview redactions, decode + redaction ≈ 29 ms against a
16.7 ms frame): when frames were only dropped by the present task, every dropped frame had already been decoded and
redacted, so dropping saved nothing; after 4 drops a late frame was shown and the clock never restarted. The result
was 1 frame in 5 on screen (7 fps) and playback 1.8× slower than real time, 12 s behind at the end of a 15 s clip.

If a command interrupts the due-time wait, the frame is kept in `_held` (resume or step wants exactly that frame); if
cancellation interrupts it, the frame is parked in `_held` so `DisposeAsync` releases it — previously it leaked.

Export shares none of this code and never drops frames ([ExportPipeline.md](ExportPipeline.md)).

## Preview redaction on its own task

`RedactionPreviewProcessor` implements `IPreviewFrameProcessor` and runs on the playback redaction task, between the
decode task and the decode-ahead buffer (joined to the decode task by a one-frame channel), on a decoded copy the
pipeline owns (the source file is never touched). Decoding the next frame overlaps with redacting this one, so per
frame the pipeline costs the slower of the two instead of their sum. Frames made stale by a seek are discarded there
without being redacted. It uses the same `RedactionPlanner`
and `IRedactionProcessor` as export, reading the detection repository (lock-protected) and the region store (lock-free
copy-on-write snapshot). Its cost is timed separately from decoding and shown in diagnostics as "Preview redaction
time" (average, 95th percentile and slowest of the last 128 frames). Processor exceptions are
caught, the first three logged, and playback continues — preview processing is best effort.

## States

```text
Starting ─► Paused ◄──► Playing ─► Ended ─(Play)─► Playing (from frame 0)
               ▲            │
               └── Step/Seek┘          any ─(exception)─► Faulted (terminal)
```

## Memory

Live preview frames are bounded by `DecodeAheadFrames + 4` (buffer, decode in flight, held frame, presenter slot,
frame being copied). See [MemoryManagement.md](MemoryManagement.md).

## Failure behaviour

A decoder exception in the decode task completes the channel with that error; the present task surfaces it as
`Faulted`, raises `StateChanged` with the error, the ViewModel disables transport commands and shows a banner. Neither
task throws into the thread pool; disposal still releases the decoder. Stream shorter than the container's frame count
→ `Ended` cleanly (a `null` end-of-stream marker, not an error).

## Alternatives considered

| Option | Why not |
|---|---|
| WPF `MediaElement` | no frame access for redaction/AI, no frame-accurate stepping |
| Decode on `DispatcherTimer` | decode on UI thread; timer resolution; freezes on large frames |
| One decoder shared by playback and analysis | conflicting seek positions and drop policies |
| Unbounded `ConcurrentQueue` between decoder and UI | grows without bound when the UI is slower than decode |
| Single worker (decode + present in one loop) | the first implementation; decode-time variation (keyframes, 4K) lands directly on presentation — replaced by the decode-ahead pair |
| Decode-ahead with `DropOldest` | would silently discard frames before the clock decides; late-frame dropping is explicit and counted instead |
| `CompositionTarget.Rendering` pull model | viable; fires every vsync even when paused — not needed at measured 0 late drops |

## Testing

`PlaybackSessionTests` (ordering, pause, seek, clamping, coalescing, steps, end/restart, short streams, faults,
late-frame dropping under slow decode, responsiveness, concurrency, disposal, ownership), `DecodeAheadTests` (buffer
bound while paused, bounded live frames with a fast producer, seek flushing, step from the buffer, `Refresh`, preview
processor failures do not stop playback), `PlaybackClockTests` (`FakeTimeProvider`) and the integration test
`Playback_session_plays_a_real_video_to_the_end_and_releases_native_frames`.
