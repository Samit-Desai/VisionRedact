using Microsoft.Extensions.Time.Testing;
using VisionRedact.Application.Media;
using VisionRedact.Application.Playback;
using VisionRedact.Application.Tests.Fakes;

namespace VisionRedact.Application.Tests.Playback;

// the decode-ahead buffer: bounded, flushed by a seek, never leaks
public sealed class DecodeAheadTests : IAsyncLifetime
{
    private readonly RecordingFrameSink _sink = new();
    private readonly List<PlaybackSession> _sessions = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }
    }

    [Fact]
    public async Task Paused_producer_decodes_ahead_only_up_to_the_buffer_capacity()
    {
        var decoder = new FakeVideoDecoder(frameCount: 10_000);
        var session = Start(decoder, new PlaybackOptions { DecodeAheadFrames = 3 });
        await _sink.WaitForFrameAsync(0);

        await Task.Delay(300); // give an unbounded design plenty of time to run away

        // frame 0 shown + 3 in the buffer + 1 in redaction (waiting in WriteAsync) + 1 in the one-frame channel
        // + 1 decoded and waiting in WriteAsync = position 7 at most
        Assert.InRange(decoder.Position, 1, 7);
        Assert.Equal(3, session.GetStatistics().DecodeAheadCount);
    }

    [Fact]
    public async Task Producer_faster_than_consumer_keeps_live_frames_bounded()
    {
        // decode is instant and presenting is paced at 60 fps, so the producer is always ahead
        var decoder = new FakeVideoDecoder(frameCount: 100_000, fps: 60);
        var session = Start(decoder, new PlaybackOptions { DecodeAheadFrames = 4 });
        await _sink.WaitForFrameAsync(0);
        session.Play();

        var maxLive = 0;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(10);
            maxLive = Math.Max(maxLive, decoder.CreatedFrames.Count(f => !f.IsDisposed));
        }

        // buffer (4) + decoding + one-frame channel + redacting + held by the consumer. the sink disposes right away
        Assert.InRange(maxLive, 1, 4 + 4);
        Assert.True(decoder.CreatedFrames.Count < 100, "Decoder must be throttled by backpressure, not run to the end.");
    }

    [Fact]
    public async Task Seek_flushes_buffered_frames_and_disposes_them()
    {
        var decoder = new FakeVideoDecoder(frameCount: 10_000);
        var session = Start(decoder, new PlaybackOptions { DecodeAheadFrames = 5 });
        await _sink.WaitForFrameAsync(0);
        await Task.Delay(100); // buffer fills with frames 1..5

        session.Seek(5_000);
        await _sink.WaitForFrameAsync(5_000);

        Assert.True(session.GetStatistics().FramesDiscardedBySeek >= 1);
        Assert.All(decoder.CreatedFrames.Where(f => f.FrameIndex is > 0 and < 5_000), f => Assert.True(f.IsDisposed));
        Assert.DoesNotContain(_sink.Presented, i => i is > 0 and < 5_000);
    }

    [Fact]
    public async Task Step_forward_uses_the_buffered_frame_without_seeking()
    {
        var decoder = new FakeVideoDecoder(frameCount: 100);
        var session = Start(decoder);
        await _sink.WaitForFrameAsync(0);
        var seeksBefore = decoder.SeekCount;

        session.Step(1);
        await _sink.WaitForFrameAsync(1);
        session.Step(1);
        await _sink.WaitForFrameAsync(2);

        Assert.Equal(seeksBefore, decoder.SeekCount);
    }

    [Fact]
    public async Task Refresh_re_decodes_and_re_presents_the_current_frame()
    {
        var session = Start(new FakeVideoDecoder(frameCount: 100));
        await _sink.WaitForFrameAsync(0);
        session.Seek(42);
        await _sink.WaitForFrameAsync(42);

        session.Refresh();

        await _sink.WaitUntilAsync(p => p.Count(i => i == 42) == 2);
    }

    [Fact]
    public async Task Preview_processor_runs_on_every_decoded_frame_and_failures_do_not_stop_playback()
    {
        var processor = new FlakyProcessor(failEvery: 3);
        var decoder = new FakeVideoDecoder(frameCount: 30, fps: 300);
        var session = Start(decoder, processor: processor);
        await _sink.WaitForFrameAsync(0);

        session.Play();
        await session.WaitForStateAsync(PlaybackState.Ended);

        Assert.True(processor.Calls >= 30);
        Assert.Equal(29, _sink.Presented[^1]);
    }

    [Fact]
    public async Task Preview_processor_time_is_measured_on_its_own()
    {
        // fake time only moves inside the processor, so each frame takes exactly 7 ms.
        // decode time isn't checked: it runs on another task and can see the processor move the clock
        var time = new FakeTimeProvider();
        var session = Start(new FakeVideoDecoder(frameCount: 100), processor: new TimedProcessor(time, _ => TimeSpan.FromMilliseconds(7)), timeProvider: time);
        await _sink.WaitForFrameAsync(0);
        await WaitUntilAsync(() => session.GetStatistics().PreviewProcessTime.Samples >= 3);

        var stats = session.GetStatistics();
        Assert.Equal(TimeSpan.FromMilliseconds(7), stats.PreviewProcessTime.Average);
        Assert.Equal(TimeSpan.FromMilliseconds(7), stats.PreviewProcessTime.P95);
        Assert.Equal(TimeSpan.FromMilliseconds(7), stats.PreviewProcessTime.Max);
    }

    [Fact]
    public async Task Preview_processor_time_shows_the_slowest_recent_frame()
    {
        var time = new FakeTimeProvider();
        var processor = new TimedProcessor(time, frame => TimeSpan.FromMilliseconds(frame == 2 ? 30 : 5));
        var session = Start(new FakeVideoDecoder(frameCount: 100), processor: processor, timeProvider: time);
        await _sink.WaitForFrameAsync(0);
        await WaitUntilAsync(() => session.GetStatistics().PreviewProcessTime.Samples >= 4);

        var redact = session.GetStatistics().PreviewProcessTime;
        Assert.Equal(TimeSpan.FromMilliseconds(30), redact.Max);
        Assert.Equal(TimeSpan.FromMilliseconds(30), redact.P95); // under 20 samples, the 95th percentile is the slowest
        Assert.InRange(redact.Average, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task The_next_frame_decodes_while_this_one_is_redacted()
    {
        // no timing: redaction holds frame 5 until the decoder has finished frame 6. if decode waited for redaction
        // (one after the other), the decoder couldn't move and the hold would time out.
        // the first version timed sleeps and failed on a busy 2-core CI runner
        var decoder = new FakeVideoDecoder(frameCount: 30, fps: 25);
        var processor = new HoldingProcessor(decoder, holdFrame: 5, until: 7);
        var session = Start(decoder, processor: processor);
        await _sink.WaitForFrameAsync(0);

        session.Play();
        await session.WaitForStateAsync(PlaybackState.Ended, TimeSpan.FromSeconds(15));

        Assert.True(processor.Held, "frame 5 never reached redaction");
        Assert.True(processor.Overlapped, "the decoder did not move on while frame 5 was being redacted");
    }

    [Fact]
    public async Task Without_a_processor_no_preview_time_is_reported()
    {
        var session = Start(new FakeVideoDecoder(frameCount: 100));
        await _sink.WaitForFrameAsync(0);

        Assert.Equal(0, session.GetStatistics().PreviewProcessTime.Samples);
    }

    private PlaybackSession Start(FakeVideoDecoder decoder, PlaybackOptions? options = null, IPreviewFrameProcessor? processor = null, TimeProvider? timeProvider = null)
    {
        var s = PlaybackSession.Start(decoder, _sink, timeProvider ?? TimeProvider.System, options: options, processor: processor);
        _sessions.Add(s);
        return s;
    }

    // polls in real time (the fake clock may never move), fails after 5 s
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met within 5 s");
            await Task.Delay(10);
        }
    }

    // holds one frame in redaction until the decoder is past it, or 5 s pass (= it never will)
    private sealed class HoldingProcessor(FakeVideoDecoder decoder, long holdFrame, long until) : IPreviewFrameProcessor
    {
        private volatile bool _held;
        private volatile bool _overlapped;

        public bool Held => _held;

        public bool Overlapped => _overlapped;

        public void Process(OwnedVideoFrame frame)
        {
            if (frame.FrameIndex != holdFrame)
            {
                return;
            }

            _held = true;
            _overlapped = SpinWait.SpinUntil(() => decoder.Position >= until, TimeSpan.FromSeconds(5));
        }
    }

    // takes a set amount of fake time per frame, so the reported durations are exact
    private sealed class TimedProcessor(FakeTimeProvider time, Func<long, TimeSpan> duration) : IPreviewFrameProcessor
    {
        public void Process(OwnedVideoFrame frame) => time.Advance(duration(frame.FrameIndex));
    }

    private sealed class FlakyProcessor(int failEvery) : IPreviewFrameProcessor
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Process(OwnedVideoFrame frame)
        {
            if (Interlocked.Increment(ref _calls) % failEvery == 0)
            {
                throw new InvalidOperationException("processor failure");
            }
        }
    }
}
