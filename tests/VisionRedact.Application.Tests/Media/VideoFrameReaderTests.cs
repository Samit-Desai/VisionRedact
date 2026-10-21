using VisionRedact.Application.Media;
using VisionRedact.Application.Tests.Fakes;

namespace VisionRedact.Application.Tests.Media;

// random frame reads on its own decoder: the right frame, few seeks, one call at a time, clean close
public class VideoFrameReaderTests
{
    private readonly FakeVideoDecoder _decoder = new(frameCount: 100);

    [Fact]
    public async Task Returns_the_requested_frame_and_disposes_it_after_projection()
    {
        var reader = CreateReader();

        var marker = await reader.ReadAsync(42, frame => new Marker(frame.GetPixels()[0]));

        Assert.Equal(42, marker!.Value);
        Assert.All(_decoder.CreatedFrames, f => Assert.True(f.IsDisposed));
    }

    [Fact]
    public async Task Reading_the_next_frame_does_not_seek()
    {
        var reader = CreateReader();

        await reader.ReadAsync(10, _ => new Marker(0));
        await reader.ReadAsync(11, _ => new Marker(0));

        Assert.Equal(1, _decoder.SeekCount); // frame 11 is where the decoder already is after reading 10
    }

    [Fact]
    public async Task Out_of_range_requests_are_clamped_to_the_video()
    {
        var reader = CreateReader();

        var marker = await reader.ReadAsync(5_000, frame => new Marker(frame.GetPixels()[0]));

        Assert.Equal(99, marker!.Value);
    }

    [Fact]
    public async Task Concurrent_requests_never_use_the_decoder_at_the_same_time()
    {
        _decoder.DecodeLatency = TimeSpan.FromMilliseconds(5);
        var reader = CreateReader();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => reader.ReadAsync(i * 3, _ => new Marker(0))));

        Assert.Equal(0, _decoder.ConcurrentCallViolations);
    }

    [Fact]
    public async Task Decoder_is_opened_lazily_and_released_on_dispose()
    {
        var opened = 0;
        var reader = new VideoFrameReader(new FakeVideoDecoderFactory(_ => { opened++; return _decoder; }), "video.mp4");
        Assert.Equal(0, opened);

        await reader.ReadAsync(0, _ => new Marker(0));
        await reader.DisposeAsync();

        Assert.Equal(1, opened);
        Assert.True(_decoder.IsDisposed);
        Assert.Null(await reader.ReadAsync(0, _ => new Marker(0)));
    }

    [Fact]
    public async Task Holds_one_frame_only_during_a_read_and_reports_the_decoder_open_until_dispose()
    {
        var reader = CreateReader();
        Assert.False(reader.IsDecoderOpen);

        var heldDuringRead = await reader.ReadAsync(7, _ => new Marker((byte)reader.FramesHeld));

        Assert.Equal(1, heldDuringRead!.Value);
        Assert.Equal(0, reader.FramesHeld);
        Assert.Equal(1, reader.PeakFramesHeld);
        Assert.True(reader.IsDecoderOpen);

        await reader.DisposeAsync();
        Assert.False(reader.IsDecoderOpen);
    }

    [Fact]
    public async Task A_failing_projection_still_releases_the_frame()
    {
        var reader = CreateReader();

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync<Marker>(3, _ => throw new InvalidOperationException("boom")));

        Assert.Equal(0, reader.FramesHeld);
        Assert.All(_decoder.CreatedFrames, f => Assert.True(f.IsDisposed));
    }

    [Fact]
    public async Task Average_read_time_measures_seek_and_decode()
    {
        _decoder.DecodeLatency = TimeSpan.FromMilliseconds(10);
        var reader = CreateReader();
        Assert.Equal(TimeSpan.Zero, reader.AverageReadTime);

        await reader.ReadAsync(20, _ => new Marker(0));
        await reader.ReadAsync(60, _ => new Marker(0));

        Assert.True(reader.AverageReadTime >= TimeSpan.FromMilliseconds(5), $"Average read time was {reader.AverageReadTime}.");
    }

    private VideoFrameReader CreateReader() => new(new FakeVideoDecoderFactory(_ => _decoder), "video.mp4");

    private sealed record Marker(byte Value);
}
