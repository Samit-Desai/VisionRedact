using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Diagnostics;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Media;

namespace VisionRedact.Application.Playback;

// the player engine: 3 tasks and 2 small bounded channels, over one decoder it owns.
// decode task -> Channel(1) -> redaction task -> Channel(DecodeAheadFrames) -> present task -> IPreviewFrameSink
// VideoWorkspace starts one per video. commands only write the mailbox and ring a doorbell, so the UI never waits.
// a seek bumps the generation, frames of an older one get thrown away, so a burst of seeks decodes only the last.
// NOTE: started as one loop (decode + present), so every slow keyframe hit the screen. then it was split in two,
// later the redaction got its own task too: a frame now costs the slower of decode and redaction, not the sum
// at most DecodeAheadFrames + 6 frames alive: decoding, Channel(1), redacting, held, sink pending, sink drawing
public sealed class PlaybackSession : IAsyncDisposable
{
    // a wait under 1 ms isn't worth a timer round trip, the Windows timer is coarse anyway
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(1);

    private readonly IVideoDecoder _decoder;
    private readonly IPreviewFrameSink _sink;
    private readonly IPreviewFrameProcessor? _processor;
    private readonly PlaybackOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly PlaybackClock _clock;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<DecodedFrame> _buffer;

    // decode -> redaction. one frame is enough to decode the next while this one is redacted,
    // for 2 more frames in memory (this slot + the one being redacted)
    private const int DecodedQueueFrames = 1;
    private readonly Channel<DecodedFrame> _decoded;

    // doorbells, never disposed on purpose: no kernel handle unless AvailableWaitHandle is used, and it removes
    // the race of a click calling Release on a disposed semaphore while the video closes.
    // the present task sleeps on this one. commands, a failing task and DisposeAsync ring it.
    // starts at 0, extra rings just pile up, DrainSignals empties them after a wake
    private readonly SemaphoreSlim _consumerWake = new(0, int.MaxValue);

    // the decode task only waits on this one at the end of the stream. RequestPosition and DisposeAsync ring it
    private readonly SemaphoreSlim _producerWake = new(0, int.MaxValue);

    private Task _producer = Task.CompletedTask;
    private Task _redactor = Task.CompletedTask;
    private Task _consumer = Task.CompletedTask;
    private int _disposed; // 1 = DisposeAsync already ran

    // ---- mailbox: any thread writes ----
    // one record swapped w/ a CAS loop (RequestPosition), so a reader never sees the generation of one seek
    // with the frame of another
    private PositionRequest _request = new(0, 0);

    // steps not done yet. commands Interlocked.Add, the present task takes them all w/ Interlocked.Exchange
    private int _pendingStep;

    private volatile bool _playRequested;

    // the clock anchor for the decode task's ShouldSkip, null while not playing.
    // one record, so generation, frame and timestamp always match
    private PacingAnchor? _pacing;

    // ---- published: the tasks write, UI and Diagnostics read (Volatile / Interlocked) ----
    private int _state = (int)PlaybackState.Starting;
    private long _currentFrame = -1;
    // generation of the frame on screen. differs from _request's while a seek is in flight
    private long _presentedGeneration = -1;
    private Exception? _fault;
    private long _framesDecoded;
    private long _framesPresented;
    private long _framesDroppedLate;
    private long _framesSkippedEarly;
    private long _framesDiscarded;
    private long _decodeTicks;
    private int _processorFailures;
    // preview processor time per frame, apart from decode time
    private readonly RecentDurations _processTimes = new();

    // for ShouldSkip, from the redaction task: its smoothed cost per frame (ticks), and how many frames it was
    // handed and hasn't finished (decode task adds, redaction task subtracts)
    private long _processCostTicks;
    private int _framesInRedaction;

    // ---- decode task only ----
    // smoothed decode cost (ticks), for ShouldSkip
    private double _decodeCostTicks;

    // ---- present task only (and DisposeAsync, once the tasks have ended) ----
    private long _generation = -1;
    private DecodedFrame? _held;
    private long _lastConsumed = -1;
    private bool _endReached;

    private PlaybackSession(IVideoDecoder decoder, IPreviewFrameSink sink, TimeProvider timeProvider, PlaybackOptions options, IPreviewFrameProcessor? processor, ILogger logger)
    {
        _decoder = decoder;
        _sink = sink;
        _processor = processor;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _clock = new PlaybackClock(timeProvider, decoder.Metadata.FramesPerSecond);
        Metadata = decoder.Metadata;
        DecoderBackend = decoder.BackendName;
        // the decode-ahead buffer in front of the present task
        _buffer = Channel.CreateBounded<DecodedFrame>(new BoundedChannelOptions(options.DecodeAheadFrames)
        {
            FullMode = BoundedChannelFullMode.Wait, // backpressure, never silent loss
            SingleReader = true, // present task
            SingleWriter = true, // redaction task
        });
        _decoded = Channel.CreateBounded<DecodedFrame>(new BoundedChannelOptions(DecodedQueueFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    // raised on the present task. handlers post to the UI themselves and return quickly
    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

    public VideoMetadata Metadata { get; }

    public string DecoderBackend { get; }

    public PlaybackState State => (PlaybackState)Volatile.Read(ref _state);

    /// <summary>Frame last handed to the sink; -1 before the first.</summary>
    public long CurrentFrameIndex => Interlocked.Read(ref _currentFrame);

    /// <summary>The seek target while a seek is in flight, else the frame on screen.</summary>
    // "this frame" actions use it (Detect this frame, a region's start/end), so they match the timeline.
    // they used the frame on screen before, so during a slow 4K seek they hit the previous one
    public long RequestedFrameIndex
    {
        get
        {
            var request = Volatile.Read(ref _request);
            return request.Generation != Interlocked.Read(ref _presentedGeneration) ? request.StartFrame : CurrentFrameIndex;
        }
    }

    public bool IsPlayRequested => _playRequested;

    /// <summary>Takes ownership of the decoder and starts the 3 tasks. Frame 0 shows like a seek.</summary>
    // static, so no work starts in a constructor
    public static PlaybackSession Start(
        IVideoDecoder decoder,
        IPreviewFrameSink sink,
        TimeProvider timeProvider,
        ILogger<PlaybackSession>? logger = null,
        PlaybackOptions? options = null,
        IPreviewFrameProcessor? processor = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options ??= PlaybackOptions.Default;
        options.Validate();

        var session = new PlaybackSession(decoder, sink, timeProvider, options, processor, (ILogger?)logger ?? NullLogger.Instance);
        var token = session._lifetime.Token;

        // Task.Run so none of the 3 loops starts on the caller's (UI) thread. decode is blocking native work
        session._producer = Task.Run(() => session.RunProducerAsync(token));
        session._redactor = Task.Run(() => session.RunRedactionAsync(token));
        session._consumer = Task.Run(() => session.RunConsumerAsync(token));
        return session;
    }

    // commands never block: they only write the mailbox and ring a doorbell, the present task does the work.
    // mostly from the UI thread, but any thread is ok (ProjectService calls Refresh from the pool)
    public void Play()
    {
        _playRequested = true;
        _consumerWake.Release();
    }

    public void Pause()
    {
        _playRequested = false;
        _consumerWake.Release();
    }

    public void TogglePlayPause()
    {
        if (_playRequested)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>Latest wins: a burst of seeks decodes only the last target.</summary>
    public void Seek(long frameIndex) => RequestPosition(Metadata.ClampFrame(frameIndex));

    /// <summary>Pauses and moves by this many frames (negative = back). Quick steps add up.</summary>
    public void Step(int frames)
    {
        if (frames == 0)
        {
            return;
        }

        _playRequested = false;

        Interlocked.Add(ref _pendingStep, frames);
        _consumerWake.Release();
    }

    // decode + redact the current frame again, e.g. a region moved while paused. does nothing while playing
    public void Refresh()
    {
        var current = CurrentFrameIndex;
        if (current >= 0 && !_playRequested)
        {
            RequestPosition(current);
        }
    }

    // for the Diagnostics panel, any thread
    public PlaybackStatistics GetStatistics()
    {
        var decoded = Interlocked.Read(ref _framesDecoded);
        return new PlaybackStatistics(
            decoded,
            Interlocked.Read(ref _framesPresented),
            Interlocked.Read(ref _framesDroppedLate),
            Interlocked.Read(ref _framesSkippedEarly),
            Interlocked.Read(ref _framesDiscarded),
            decoded == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Interlocked.Read(ref _decodeTicks) / decoded),
            _buffer.Reader.CanCount ? _buffer.Reader.Count : 0,
            _options.DecodeAheadFrames,
            _processTimes.Summarize());
    }

    // cancel -> wait for all 3 tasks -> free every frame left -> only then the decoder.
    // awaited, not blocking: the caller is usually the UI thread, and the decode task may be inside native code
    public async ValueTask DisposeAsync()
    {
        // closing the video and closing the app can race, only the first call gets through
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // cancel, and ring both doorbells so a sleeping task notices right away
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _consumerWake.Release();
        _producerWake.Release();

        try
        {
            await Task.WhenAll(_producer, _redactor, _consumer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // each loop catches its own errors, this is only so dispose always finishes
            _logger.LogError(ex, "Playback task ended with an unexpected exception during disposal.");
        }

        // the frame the present task parked, and anything still in the 2 channels
        _held?.Frame?.Dispose();
        _held = null;
        while (_buffer.Reader.TryRead(out var item))
        {
            item.Frame?.Dispose();
        }

        while (_decoded.Reader.TryRead(out var item))
        {
            item.Frame?.Dispose();
        }

        // careful: only now. disposed while the decode task is inside ReadNext, OpenCV would use freed memory
        _decoder.Dispose();
        _lifetime.Dispose();
    }

    // ---- decode task: the only code that uses the decoder while the session runs ----
    // new seek? -> skip or decode one frame -> hand it to the redaction task (waits while it's busy)
    private async Task RunProducerAsync(CancellationToken cancellationToken)
    {
        var writer = _decoded.Writer;
        long generation = -1; // the generation we decode for
        var atEnd = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // new generation = a seek happened, move the decoder there (unless it's already there)
                var request = Volatile.Read(ref _request);
                if (request.Generation != generation)
                {
                    generation = request.Generation;
                    atEnd = false;
                    if (_decoder.Position != request.StartFrame)
                    {
                        _decoder.Seek(request.StartFrame);
                    }
                }

                if (atEnd)
                {
                    // end of the stream for this generation, sleep until a seek (or dispose)
                    await _producerWake.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // late anyway? skip it before the color conversion and the redaction. Skip still decodes it,
                // later frames depend on it. if Skip hits the end, Decode below returns the end marker
                if (ShouldSkip(generation, _decoder.Position) && _decoder.Skip())
                {
                    Interlocked.Increment(ref _framesSkippedEarly);
                    Interlocked.Increment(ref _framesDroppedLate);
                    continue;
                }

                var frame = Decode();
                atEnd = frame is null; // null = end marker, passed on to the present task
                Interlocked.Increment(ref _framesInRedaction); // counted from now, it waits for the redaction task
                try
                {
                    // waits while Channel(1) is full (the redaction task is still busy)
                    await writer.WriteAsync(new DecodedFrame(generation, frame), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    Interlocked.Decrement(ref _framesInRedaction);
                    frame?.Dispose(); // not handed over -> still ours
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // decode failed: keep the error, close the channel w/ it, wake the present task so it shows Faulted
            _logger.LogError(ex, "Decoding failed for {Path}.", Metadata.SourcePath);
            Volatile.Write(ref _fault, ex);
            writer.TryComplete(ex);
            _consumerWake.Release();
        }
    }

    // ReadNext, timed
    private OwnedVideoFrame? Decode()
    {
        var started = _timeProvider.GetTimestamp();
        var frame = _decoder.ReadNext();
        var elapsed = _timeProvider.GetElapsedTime(started).Ticks;

        Interlocked.Add(ref _decodeTicks, elapsed);
        if (frame is null)
        {
            return null;
        }

        Interlocked.Increment(ref _framesDecoded);
        // smoothed, so one slow keyframe doesn't swing the skip decisions
        _decodeCostTicks = _decodeCostTicks == 0 ? elapsed : (_decodeCostTicks * 0.8) + (elapsed * 0.2);
        return frame;
    }

    // true when this frame, decoded and redacted in full, would reach the screen more than one frame late,
    // so the present task would drop it anyway (PlaybackClock.IsLate).
    // good find - drops used to happen only there, after the work, so a drop saved nothing and 4K60 fell further
    // and further behind real time. deciding here, before the work, fixed it (ADR-0009 update)
    // only while playing this generation, never the last frame, never the first after a seek (no anchor yet).
    // the MaxConsecutiveLateDrops cap counts every frame since the one on screen, some of them still in the
    // buffers, so it errs toward decoding
    private bool ShouldSkip(long generation, long frameIndex)
    {
        var anchor = Volatile.Read(ref _pacing);
        if (anchor is null || anchor.Generation != generation || frameIndex >= Metadata.LastFrameIndex
            || frameIndex - CurrentFrameIndex - 1 >= _options.MaxConsecutiveLateDrops)
        {
            return false;
        }

        // decoding it overlaps redacting the frames already handed over, so it reaches the redaction task after the
        // longer of the two, then takes one redaction itself. measured from the anchor like PlaybackClock
        double redact = Volatile.Read(ref _processCostTicks);
        var queued = Volatile.Read(ref _framesInRedaction);
        var readyIn = TimeSpan.FromTicks((long)(Math.Max(_decodeCostTicks, queued * redact) + redact));
        var ready = _timeProvider.GetElapsedTime(anchor.Timestamp) + readyIn;
        var due = TimeSpan.FromSeconds((frameIndex - anchor.Frame) / Metadata.FramesPerSecond);
        return ready - due > _clock.FrameDuration;
    }

    // ---- redaction task: the preview processor, on its own task so it overlaps the next decode ----
    // take a frame -> redact it (unless a seek made it stale) -> into the decode-ahead buffer
    private async Task RunRedactionAsync(CancellationToken cancellationToken)
    {
        var reader = _decoded.Reader;
        var writer = _buffer.Writer;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var item = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (item.Generation != Volatile.Read(ref _request).Generation)
                {
                    // decoded before the latest seek: the present task would throw it away, so don't redact it
                    Interlocked.Decrement(ref _framesInRedaction);
                    if (item.Frame is { } stale)
                    {
                        stale.Dispose();
                        Interlocked.Increment(ref _framesDiscarded);
                    }

                    continue;
                }

                if (item.Frame is { } frame)
                {
                    Process(frame);
                }

                Interlocked.Decrement(ref _framesInRedaction);
                try
                {
                    // waits while the decode-ahead buffer is full
                    await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    item.Frame?.Dispose(); // not handed over -> still ours
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException ex)
        {
            // the decode task failed and closed Channel(1) w/ the error: pass it on, so the present task shows Faulted
            writer.TryComplete(Volatile.Read(ref _fault) ?? ex.InnerException ?? ex);
            _consumerWake.Release();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preview redaction stage failed for {Path}.", Metadata.SourcePath);
            Volatile.Write(ref _fault, ex);
            writer.TryComplete(ex);
            _consumerWake.Release();
        }
    }

    // timed for the Diagnostics panel and for ShouldSkip
    private void Process(OwnedVideoFrame frame)
    {
        if (_processor is null)
        {
            return;
        }

        var processing = _timeProvider.GetTimestamp();
        try
        {
            _processor.Process(frame);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // best effort, it never stops playback. logs the first 3 only
            if (Interlocked.Increment(ref _processorFailures) <= 3)
            {
                _logger.LogWarning(ex, "Preview frame processor failed on frame {Frame}.", frame.FrameIndex);
            }
        }

        var elapsed = _timeProvider.GetElapsedTime(processing).Ticks;
        _processTimes.Add(TimeSpan.FromTicks(elapsed));

        // smoothed, for ShouldSkip. only this task writes it
        var smoothed = Volatile.Read(ref _processCostTicks);
        Volatile.Write(ref _processCostTicks, smoothed == 0 ? elapsed : (long)((smoothed * 0.8) + (elapsed * 0.2)));
    }

    // ---- present task: commands, timing, presenting ----
    // one thing per loop, in this order: fault -> seek -> step -> play -> sleep (paused). so a seek always wins
    private async Task RunConsumerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // a task failed (decode or redaction)
                if (Volatile.Read(ref _fault) is { } fault)
                {
                    EnterFaulted(fault);
                    return;
                }

                // a seek is waiting: show its target
                var request = Volatile.Read(ref _request);
                if (request.Generation != _generation)
                {
                    await PresentSeekTargetAsync(request.Generation, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // take all pending steps in one go, a step added at the same moment isn't lost
                var step = Interlocked.Exchange(ref _pendingStep, 0);
                if (step != 0)
                {
                    await StepAsync(step, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_playRequested)
                {
                    await PlayNextFrameAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // paused: publish the state, sleep until a command rings
                StopClock();
                SetState(_endReached ? PlaybackState.Ended : PlaybackState.Paused);
                await _consumerWake.WaitAsync(cancellationToken).ConfigureAwait(false);
                DrainSignals(_consumerWake); // many rings = one wake, the loop re-reads the mailbox anyway
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException ex)
        {
            // the redaction task closed the buffer w/ an error (its own or the decode task's)
            EnterFaulted(Volatile.Read(ref _fault) ?? ex.InnerException ?? ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playback failed for {Path} at frame {Frame}.", Metadata.SourcePath, CurrentFrameIndex);
            EnterFaulted(ex);
        }
    }

    // adopt the new generation (older frames are stale now), then show its first frame. play continues from there
    private async Task PresentSeekTargetAsync(long generation, CancellationToken cancellationToken)
    {
        _generation = generation;
        StopClock(); // restarts from the new position when play continues
        var item = await ReadCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
        if (item is not { } decoded)
        {
            return; // a newer seek came in while we waited
        }

        if (decoded.Frame is null)
        {
            // the container's frame count was too high, the stream ended before the target
            _endReached = true;
            _logger.LogWarning("No frame could be decoded at the requested position in {Path}.", Metadata.SourcePath);
            return;
        }

        _endReached = false;
        Present(decoded.Frame);
    }

    // paused. +1 takes the next frame from the buffer, anything else (back, bigger jumps) becomes a seek
    private async Task StepAsync(int step, CancellationToken cancellationToken)
    {
        StopClock();
        var current = CurrentFrameIndex;
        var target = Metadata.ClampFrame(current + step);
        if (target == current || (step > 0 && _endReached))
        {
            return; // nowhere to go, or already at the real end of the stream
        }

        // fast path: the next frame is already in the buffer, no seek
        if (target == current + 1 && _lastConsumed == current)
        {
            var item = await ReadCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
            if (item is not { } decoded)
            {
                return;
            }

            if (decoded.Frame is null)
            {
                _endReached = true;
                return;
            }

            if (decoded.Frame.FrameIndex != target)
            {
                // the decode task skipped it while playing, so seek to it
                decoded.Frame.Dispose();
                RequestPosition(target);
                return;
            }

            Present(decoded.Frame);
            return;
        }

        RequestPosition(target);
    }

    // one frame of playback: take it, drop it if it's already late, else wait for its due time
    // (commands can still wake us) and present it
    private async Task PlayNextFrameAsync(CancellationToken cancellationToken)
    {
        if (!_clock.IsRunning && (_endReached || CurrentFrameIndex >= Metadata.LastFrameIndex))
        {
            // Play at the end starts again from frame 0
            _endReached = false;
            RequestPosition(0);
            return;
        }

        if (!_clock.IsRunning && _lastConsumed != CurrentFrameIndex)
        {
            // frames after the one on screen were dropped before the pause: seek to exactly the next one
            RequestPosition(CurrentFrameIndex + 1);
            return;
        }

        // null item = a seek came in, null frame = end of stream
        var item = await ReadCurrentGenerationAsync(cancellationToken).ConfigureAwait(false);
        if (item is not { } decoded)
        {
            return;
        }

        if (decoded.Frame is not { } frame)
        {
            EndPlayback();
            return;
        }

        if (!_clock.IsRunning)
        {
            // resuming: the decode task may have skipped frames just before the pause, so the next one in the
            // buffer may not follow the one on screen. realign, don't jump over them
            var shown = CurrentFrameIndex;
            if (shown >= 0 && frame.FrameIndex != shown + 1)
            {
                frame.Dispose();
                RequestPosition(shown + 1);
                return;
            }

            // anchor on the first frame we really have, so the decode delay after a seek doesn't count as late
            StartClock(frame.FrameIndex);
            SetState(PlaybackState.Playing);
        }
        else if (_clock.IsLate(frame.FrameIndex) && frame.FrameIndex < Metadata.LastFrameIndex) // never the last one, playback must end on it
        {
            // frames left out in a row since the one on screen, skipped early or dropped here
            if (frame.FrameIndex - CurrentFrameIndex - 1 < _options.MaxConsecutiveLateDrops)
            {
                _lastConsumed = frame.FrameIndex;
                frame.Dispose(); // its slot has passed, showing it would only add delay
                Interlocked.Increment(ref _framesDroppedLate);
                return;
            }

            // too many left out: show this one anyway and restart the clock from it.
            // it didn't restart at first, so the delay just kept growing (fixed together w/ ShouldSkip)
            StartClock(frame.FrameIndex);
        }

        // one wait, two jobs: sleep until the due time, and a command (Pause/Seek/Step) wakes it early
        while (true)
        {
            var wait = _clock.TimeUntilDue(frame.FrameIndex);
            if (wait < MinimumWait)
            {
                break;
            }

            // false = slept the full wait, true = a command rang
            bool signaled;
            try
            {
                signaled = await _consumerWake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _held = decoded; // canceled mid-wait: park it so DisposeAsync frees it (this frame used to leak)
                throw;
            }

            if (signaled)
            {
                DrainSignals(_consumerWake);
                if (HasPendingCommands || !_playRequested)
                {
                    _held = decoded; // keep it, resume or step +1 wants exactly this frame
                    return;
                }

                // woken but nothing changed (e.g. Play again): keep waiting
            }
        }

        Present(frame);
    }

    // next frame of our generation, the held one first. stale frames get disposed on the way.
    // null = a newer generation showed up, back to the main loop for the seek
    private async ValueTask<DecodedFrame?> ReadCurrentGenerationAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            DecodedFrame item;
            if (_held is { } held)
            {
                _held = null;
                item = held;
            }
            else
            {
                item = await _buffer.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            if (item.Generation == _generation)
            {
                return item;
            }

            if (item.Generation > _generation)
            {
                _held = item; // for a seek the main loop hasn't handled yet
                return null;
            }

            item.Frame?.Dispose(); // stale, decoded before the latest seek
            Interlocked.Increment(ref _framesDiscarded);
        }
    }

    // new seek target w/ the next generation, then ring both doorbells. each one replaces the last (latest wins)
    private void RequestPosition(long startFrame)
    {
        // CAS loop: commands and the present task (step, realign, Play at the end) can post at the same time.
        // each request adds exactly 1 to the generation, no lock
        PositionRequest current, next;
        do
        {
            current = Volatile.Read(ref _request);
            next = new PositionRequest(current.Generation + 1, startFrame);
        }
        while (Interlocked.CompareExchange(ref _request, next, current) != current);

        // the decode task may sleep at the end, the present task may be paused or waiting for a due time
        _producerWake.Release();
        _consumerWake.Release();
    }

    // (re)starts the clock and publishes the anchor for ShouldSkip
    private void StartClock(long anchorFrame)
    {
        _clock.Start(anchorFrame);
        Volatile.Write(ref _pacing, new PacingAnchor(_generation, anchorFrame, _clock.AnchorTimestamp));
    }

    // no anchor = the decode task skips nothing (paused, seeking, stepping)
    private void StopClock()
    {
        _clock.Stop();
        Volatile.Write(ref _pacing, null);
    }

    private void EndPlayback()
    {
        _playRequested = false;
        _endReached = true;
        StopClock();
        SetState(PlaybackState.Ended);
    }

    private void Present(OwnedVideoFrame frame)
    {
        _lastConsumed = frame.FrameIndex;

        Interlocked.Exchange(ref _currentFrame, frame.FrameIndex);
        Interlocked.Exchange(ref _presentedGeneration, _generation);
        Interlocked.Increment(ref _framesPresented);
        try
        {
            _sink.Present(frame); // ownership transferred
        }
        catch
        {
            frame.Dispose(); // the sink broke its contract. a second Dispose is harmless
            throw;
        }
    }

    // terminal: stop playing, publish Faulted w/ the error
    private void EnterFaulted(Exception error)
    {
        _playRequested = false;
        Volatile.Write(ref _fault, error);
        SetState(PlaybackState.Faulted, error);
    }

    private bool HasPendingCommands =>
        Volatile.Read(ref _request).Generation != _generation || Volatile.Read(ref _pendingStep) != 0;

    // empties the doorbell. Wait(0) never blocks, it just takes one ring.
    // after a wake the whole mailbox is re-read, so extra rings would only cost useless loops
    private static void DrainSignals(SemaphoreSlim signal)
    {
        while (signal.Wait(0))
        {
        }
    }

    // raises StateChanged only on a real change
    private void SetState(PlaybackState state, Exception? error = null)
    {
        var previous = (PlaybackState)Interlocked.Exchange(ref _state, (int)state);
        if (previous == state)
        {
            return;
        }

        try
        {
            StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(previous, state, error));
        }
        catch (Exception ex)
        {
            // a bad handler must not kill playback
            _logger.LogError(ex, "A PlaybackSession.StateChanged handler threw.");
        }
    }

    // the present task's clock anchor, for the decode task: frame Frame of generation Generation was due at
    // Timestamp (TimeProvider units). one record = one consistent read
    private sealed record PacingAnchor(long Generation, long Frame, long Timestamp);

    // one seek in the mailbox. a class on purpose, so CompareExchange swaps both fields as one
    private sealed record PositionRequest(long Generation, long StartFrame);

    // Frame null = end of stream for that generation
    private readonly record struct DecodedFrame(long Generation, OwnedVideoFrame? Frame);
}
