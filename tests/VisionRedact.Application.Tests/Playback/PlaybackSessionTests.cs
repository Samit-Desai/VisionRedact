using System.Diagnostics;
using VisionRedact.Application.Playback;
using VisionRedact.Application.Tests.Fakes;

namespace VisionRedact.Application.Tests.Playback;

// the playback worker against a fake decoder, on the real clock. most tests check only order, ownership and state.
// the two Timing ones do check wall-clock limits, so CI filters them out
public sealed class PlaybackSessionTests : IAsyncLifetime
{
    private readonly RecordingFrameSink _sink = new();
    private readonly List<PlaybackSession> _sessions = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Start_presents_the_first_frame_and_pauses()
    {
        var session = Start(new FakeVideoDecoder());

        await _sink.WaitForFrameAsync(0);
        await session.WaitForStateAsync(PlaybackState.Paused);

        Assert.Equal(0, session.CurrentFrameIndex);
    }

    [Fact]
    public async Task Play_presents_frames_in_order_until_the_end()
    {
        var session = Start(new FakeVideoDecoder(frameCount: 30, fps: 200));
        await _sink.WaitForFrameAsync(0);

        session.Play();
        await session.WaitForStateAsync(PlaybackState.Ended);

        var presented = _sink.Presented;
        Assert.Equal(29, presented[^1]);
        AssertStrictlyIncreasing(presented);
        Assert.Equal(presented.Select(i => (byte)i), _sink.Markers); // pixel content matches the index
    }

    [Fact]
    public async Task Pause_stops_presenting_frames()
    {
        var session = Start(new FakeVideoDecoder(frameCount: 10_000, fps: 100));
        await _sink.WaitForFrameAsync(0);

        session.Play();
        await _sink.WaitUntilAsync(p => p.Count >= 5);
        session.Pause();
        await session.WaitForStateAsync(PlaybackState.Paused);
        var countAtPause = _sink.Presented.Count;

        await Task.Delay(150);

        Assert.Equal(countAtPause, _sink.Presented.Count);
    }

    [Fact]
    public async Task Seek_while_paused_presents_the_target_frame()
    {
        var session = Start(new FakeVideoDecoder(frameCount: 1_000));
        await _sink.WaitForFrameAsync(0);

        session.Seek(640);

        await _sink.WaitForFrameAsync(640);
        Assert.Equal(640, session.CurrentFrameIndex);
    }

    [Fact]
    public async Task Requested_frame_is_the_seek_target_while_the_seek_is_in_flight()
    {
        // a slow seek: "this frame" must already mean the target, not the frame still on screen.
        // "Detect this frame" used to act on the old frame while a long 4K seek was running
        var session = Start(new FakeVideoDecoder(frameCount: 1_000, decodeLatency: TimeSpan.FromMilliseconds(300)));
        await _sink.WaitForFrameAsync(0);

        session.Seek(640);

        Assert.Equal(640, session.RequestedFrameIndex);
        Assert.Equal(0, session.CurrentFrameIndex);
        await _sink.WaitForFrameAsync(640);
        Assert.Equal(640, session.CurrentFrameIndex);
        Assert.Equal(640, session.RequestedFrameIndex);
    }

    [Fact]
    public async Task Requested_frame_follows_playback_when_no_seek_is_pending()
    {
        var session = Start(new FakeVideoDecoder(frameCount: 30, fps: 200));
        await _sink.WaitForFrameAsync(0);

        session.Play();
        await session.WaitForStateAsync(PlaybackState.Ended);

        Assert.Equal(session.CurrentFrameIndex, session.RequestedFrameIndex);
    }

    [Fact]
    public async Task Seek_is_clamped_to_the_video()
    {
        var session = Start(new FakeVideoDecoder(frameCount: 50));
        await _sink.WaitForFrameAsync(0);

        session.Seek(10_000);

        await _sink.WaitForFrameAsync(49);
    }

    [Fact]
    public async Task Rapid_seeks_are_coalesced_so_only_the_latest_target_is_decoded()
    {
        // scrubbing: 100 seeks come in while each decode is slow
        var decoder = new FakeVideoDecoder(frameCount: 1_000, decodeLatency: TimeSpan.FromMilliseconds(30));
        var session = Start(decoder);
        await _sink.WaitForFrameAsync(0);

        for (var target = 1; target <= 100; target++)
        {
            session.Seek(target * 5);
        }

        await _sink.WaitForFrameAsync(500);

        // w/o coalescing that's 101 decodes. latest wins needs only a handful
        Assert.True(_sink.Presented.Count <= 4, $"Expected coalesced seeks, but {_sink.Presented.Count} frames were presented.");
    }

    [Fact]
    public async Task Step_moves_one_frame_forward_and_backward_and_pauses()
    {
        var session = Start(new FakeVideoDecoder(frameCount: 100));
        await _sink.WaitForFrameAsync(0);
        session.Seek(10);
        await _sink.WaitForFrameAsync(10);

        session.Step(1);
        await _sink.WaitForFrameAsync(11);
        session.Step(-1);
        await _sink.WaitForFrameAsync(10);
        session.Step(-1);
        await _sink.WaitForFrameAsync(9);

        await session.WaitForStateAsync(PlaybackState.Paused);
    }

    [Fact]
    public async Task Step_backward_at_the_first_frame_stays_put()
    {
        var session = Start(new FakeVideoDecoder(frameCount: 100));
        await _sink.WaitForFrameAsync(0);

        session.Step(-1);
        await session.WaitForStateAsync(PlaybackState.Paused);
        await Task.Delay(50);

        Assert.Equal([0L], _sink.Presented);
    }

    [Fact]
    public async Task Play_after_the_end_restarts_from_the_beginning()
    {
        var session = Start(new FakeVideoDecoder(frameCount: 10, fps: 200));
        await _sink.WaitForFrameAsync(0);
        session.Play();
        await session.WaitForStateAsync(PlaybackState.Ended);

        session.Play();

        await _sink.WaitUntilAsync(p => p.Count(i => i == 0) == 2); // frame 0 presented a second time
        await session.WaitForStateAsync(PlaybackState.Ended);
    }

    [Fact]
    public async Task Stream_shorter_than_reported_frame_count_ends_cleanly()
    {
        // the container says 100 frames, only 20 decode (common w/ cut-off files)
        var session = Start(new FakeVideoDecoder(frameCount: 100, fps: 200, decodableFrames: 20));
        await _sink.WaitForFrameAsync(0);

        session.Play();
        await session.WaitForStateAsync(PlaybackState.Ended);

        Assert.Equal(19, _sink.Presented[^1]);
    }

    [Fact]
    public async Task Decoder_failure_faults_the_session_instead_of_crashing()
    {
        var decoder = new FakeVideoDecoder(frameCount: 100, fps: 50) { FailAtFrame = 5 };
        var session = Start(decoder);
        Exception? reported = null;
        session.StateChanged += (_, e) => reported ??= e.Error;
        await _sink.WaitForFrameAsync(0);

        session.Play();
        await session.WaitForStateAsync(PlaybackState.Faulted);

        Assert.IsType<InvalidOperationException>(reported);
        Assert.InRange(session.CurrentFrameIndex, 0, 4);

        session.Play(); // commands on a faulted session are harmless no-ops
        await session.DisposeAsync();
        Assert.True(decoder.IsDisposed);
    }

    [Fact]
    public async Task Slow_decoding_skips_late_frames_but_keeps_order()
    {
        // 100 fps (10 ms a frame) w/ 25 ms decodes: decode can't keep up
        var decoder = new FakeVideoDecoder(frameCount: 60, fps: 100, decodeLatency: TimeSpan.FromMilliseconds(25));
        var session = Start(decoder);
        await _sink.WaitForFrameAsync(0);

        session.Play();
        await session.WaitForStateAsync(PlaybackState.Ended);

        var stats = session.GetStatistics();
        Assert.True(stats.FramesDroppedLate > 0, "Expected late frames to be skipped.");
        Assert.True(stats.FramesPresented < 60);
        AssertStrictlyIncreasing(_sink.Presented);
    }

    [Fact]
    [Trait("Category", "Timing")] // wall-clock limit: CI filters it out, run it locally
    public async Task Slow_decoding_keeps_real_time_by_skipping_frames_before_decoding_them()
    {
        // decode is too slow but skipping is cheap, so the producer can only keep up by skipping.
        // before the skip-early fix every frame was decoded first and dropped late after, so playback fell far behind
        var decoder = new FakeVideoDecoder(frameCount: 200, fps: 100, decodeLatency: TimeSpan.FromMilliseconds(25));
        var session = Start(decoder);
        await _sink.WaitForFrameAsync(0);

        var stopwatch = Stopwatch.StartNew();
        session.Play();
        await session.WaitForStateAsync(PlaybackState.Ended, TimeSpan.FromSeconds(15));
        stopwatch.Stop();

        var stats = session.GetStatistics();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3.5), $"A 2 s clip took {stopwatch.Elapsed.TotalSeconds:0.0} s: playback fell behind real time.");
        Assert.True(stats.FramesSkippedEarly > 0, "Expected frames to be skipped before decoding.");
        Assert.True(stats.FramesDecoded < 200, $"Decoded {stats.FramesDecoded} of 200: skipped frames were decoded anyway.");
        Assert.Equal(199, _sink.Presented[^1]); // the last frame is never left out
        AssertStrictlyIncreasing(_sink.Presented);
        AssertGapsAtMost(_sink.Presented, PlaybackOptions.Default.MaxConsecutiveLateDrops + 1);
    }

    [Fact]
    public async Task Step_after_skipped_frames_shows_exactly_the_next_frame()
    {
        // skipped frames leave gaps in the buffer, so Step +1 must still land on the very next frame
        var decoder = new FakeVideoDecoder(frameCount: 2_000, fps: 100, decodeLatency: TimeSpan.FromMilliseconds(25));
        var session = Start(decoder);
        await _sink.WaitForFrameAsync(0);
        session.Play();
        await _sink.WaitUntilAsync(p => p.Count >= 15); // the producer is behind and skipping by now
        session.Pause();
        await session.WaitForStateAsync(PlaybackState.Paused);
        var current = session.CurrentFrameIndex;

        session.Step(1);

        await _sink.WaitForFrameAsync(current + 1);
        Assert.Equal(current + 1, _sink.Presented[^1]);
    }

    [Fact]
    public async Task Resume_after_skipped_frames_continues_from_the_next_frame()
    {
        var decoder = new FakeVideoDecoder(frameCount: 2_000, fps: 100, decodeLatency: TimeSpan.FromMilliseconds(25));
        var session = Start(decoder);
        await _sink.WaitForFrameAsync(0);
        session.Play();
        await _sink.WaitUntilAsync(p => p.Count >= 15);
        session.Pause();
        await session.WaitForStateAsync(PlaybackState.Paused);
        var current = session.CurrentFrameIndex;
        var shownBefore = _sink.Presented.Count;

        session.Play();

        await _sink.WaitUntilAsync(p => p.Count > shownBefore);
        Assert.Equal(current + 1, _sink.Presented[shownBefore]); // no jump over frames skipped before the pause
    }

    [Fact]
    [Trait("Category", "Timing")] // wall-clock limit: CI filters it out, run it locally
    public async Task Commands_return_immediately_even_while_the_decoder_is_busy()
    {
        // the UI thread calls these, they must never wait for a slow native decode
        var decoder = new FakeVideoDecoder(frameCount: 1_000, decodeLatency: TimeSpan.FromMilliseconds(200));
        var session = Start(decoder);

        var stopwatch = Stopwatch.StartNew();
        session.Seek(500);
        session.Play();
        session.Pause();
        session.Step(1);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 50, $"Commands blocked for {stopwatch.ElapsedMilliseconds} ms.");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Decoder_is_never_called_concurrently_under_command_storms()
    {
        var decoder = new FakeVideoDecoder(frameCount: 5_000, fps: 500, decodeLatency: TimeSpan.FromMilliseconds(1));
        var session = Start(decoder);
        await _sink.WaitForFrameAsync(0);

        // many threads hitting the public API at once (in the app: the UI thread + timers)
        await Parallel.ForAsync(0, 400, async (i, ct) =>
        {
            switch (i % 5)
            {
                case 0: session.Play(); break;
                case 1: session.Seek(i * 7); break;
                case 2: session.Step(i % 2 == 0 ? 1 : -1); break;
                case 3: session.Pause(); break;
                default: session.TogglePlayPause(); break;
            }

            await Task.Yield();
        });

        session.Seek(4_321);
        await _sink.WaitForFrameAsync(4_321);

        Assert.Equal(0, decoder.ConcurrentCallViolations);
        Assert.Equal(0, _sink.ReceivedDisposedFrames);
    }

    [Fact]
    public async Task DisposeAsync_during_playback_stops_the_worker_and_releases_every_frame()
    {
        var decoder = new FakeVideoDecoder(frameCount: 10_000, fps: 60, decodeLatency: TimeSpan.FromMilliseconds(2));
        var session = PlaybackSession.Start(decoder, _sink, TimeProvider.System);
        await _sink.WaitForFrameAsync(0);
        session.Play();
        await _sink.WaitUntilAsync(p => p.Count >= 5);

        await session.DisposeAsync();

        Assert.True(decoder.IsDisposed);
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed, $"{f} leaked"));

        var presentedAfterDispose = _sink.Presented.Count;
        await Task.Delay(100);
        Assert.Equal(presentedAfterDispose, _sink.Presented.Count);
    }

    [Fact]
    public async Task Every_decoded_frame_is_disposed_exactly_once_across_a_mixed_session()
    {
        var decoder = new FakeVideoDecoder(frameCount: 300, fps: 200);
        var session = PlaybackSession.Start(decoder, _sink, TimeProvider.System);
        await _sink.WaitForFrameAsync(0);

        session.Play();
        await _sink.WaitUntilAsync(p => p.Count >= 20);
        session.Pause();
        session.Seek(150);
        await _sink.WaitForFrameAsync(150);
        session.Play();
        await _sink.WaitUntilAsync(p => p[^1] >= 170);
        session.Step(-3);
        await session.WaitForStateAsync(PlaybackState.Paused);
        await session.DisposeAsync();

        Assert.NotEmpty(decoder.CreatedFrames);
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed, $"{f} leaked"));
        Assert.Equal(0, _sink.ReceivedDisposedFrames);
    }

    private PlaybackSession Start(FakeVideoDecoder decoder)
    {
        var session = PlaybackSession.Start(decoder, _sink, TimeProvider.System);
        _sessions.Add(session);
        return session;
    }

    private static void AssertGapsAtMost(IReadOnlyList<long> values, long maxGap)
    {
        for (var i = 1; i < values.Count; i++)
        {
            Assert.True(values[i] - values[i - 1] <= maxGap, $"Gap of {values[i] - values[i - 1]} frames between {values[i - 1]} and {values[i]} (max {maxGap}).");
        }
    }

    private static void AssertStrictlyIncreasing(IReadOnlyList<long> values)
    {
        for (var i = 1; i < values.Count; i++)
        {
            Assert.True(values[i] > values[i - 1], $"Frame order violated at position {i}: {values[i - 1]} then {values[i]}.");
        }
    }
}
