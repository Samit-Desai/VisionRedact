using VisionRedact.Application.Media;
using VisionRedact.Application.Playback;
using VisionRedact.Infrastructure.Video;

// one test at a time: tests compare process-wide counts before and after (LiveInstances, ffmpeg processes)
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VisionRedact.IntegrationTests;

// through the native boundary for real: Emgu CV -> cvextern.dll -> OpenCV videoio
public sealed class EmguVideoDecoderTests : IClassFixture<SyntheticVideo>
{
    private const int JpegTolerance = 3;
    private readonly SyntheticVideo _video;
    private readonly EmguVideoDecoderFactory _factory = new();

    public EmguVideoDecoderTests(SyntheticVideo video)
    {
        _video = video;
    }

    [Fact]
    public void Native_runtime_loads_and_reports_video_backends()
    {
        var status = new EmguRuntimeProbe().Probe();

        Assert.True(status.IsAvailable, status.ErrorDetail);
        Assert.NotEmpty(status.VideoBackends);
    }

    [Fact]
    public void Open_reads_metadata_from_the_container()
    {
        using var decoder = _factory.Open(_video.FilePath);
        var m = decoder.Metadata;

        Assert.Equal(SyntheticVideo.Width, m.Width);
        Assert.Equal(SyntheticVideo.Height, m.Height);
        Assert.Equal(SyntheticVideo.Fps, m.FramesPerSecond, precision: 3);
        Assert.Equal(SyntheticVideo.FrameCount, m.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(2), m.Duration);
        Assert.Equal(new FileInfo(_video.FilePath).Length, m.FileSizeBytes);
        Assert.Equal("MJPG", m.Codec);
    }

    [Fact]
    public void ReadNext_decodes_every_frame_in_order_with_correct_content()
    {
        using var decoder = _factory.Open(_video.FilePath);
        var liveBefore = OwnedVideoFrame.LiveInstances;

        var count = 0;
        while (decoder.ReadNext() is { } frame)
        {
            using (frame)
            {
                Assert.Equal(count, frame.FrameIndex);
                Assert.Equal(SyntheticVideo.Width, frame.Width);
                Assert.Equal(FramePixelFormat.Bgr24, frame.PixelFormat);
                AssertGray(frame, SyntheticVideo.ExpectedGrayLevel(count));
            }

            count++;
        }

        Assert.Equal(SyntheticVideo.FrameCount, count);
        Assert.Equal(liveBefore, OwnedVideoFrame.LiveInstances); // every Mat released
    }

    [Fact]
    public void Frames_are_independently_owned_and_not_overwritten_by_later_reads()
    {
        // the QueryFrame trap: a queued frame must keep its pixels after the decoder moves on
        using var decoder = _factory.Open(_video.FilePath);
        using var first = decoder.ReadNext()!;
        using var second = decoder.ReadNext()!;
        using var third = decoder.ReadNext()!;

        AssertGray(first, SyntheticVideo.ExpectedGrayLevel(0));
        AssertGray(second, SyntheticVideo.ExpectedGrayLevel(1));
        AssertGray(third, SyntheticVideo.ExpectedGrayLevel(2));
        Assert.NotEqual(first.DataPointer, second.DataPointer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(40)]
    [InlineData(59)]
    public void Seek_returns_the_requested_frame(long target)
    {
        using var decoder = _factory.Open(_video.FilePath);
        decoder.ReadNext()?.Dispose();

        decoder.Seek(target);
        using var frame = decoder.ReadNext();

        Assert.NotNull(frame);
        Assert.Equal(target, frame.FrameIndex);
        AssertGray(frame, SyntheticVideo.ExpectedGrayLevel(target));
    }

    [Fact]
    public void Skip_advances_without_producing_a_frame()
    {
        using var decoder = _factory.Open(_video.FilePath);

        Assert.True(decoder.Skip());
        Assert.True(decoder.Skip());
        using var frame = decoder.ReadNext()!;

        Assert.Equal(2, frame.FrameIndex);
        AssertGray(frame, SyntheticVideo.ExpectedGrayLevel(2));
    }

    [Fact]
    public void Missing_file_is_reported_as_FileNotFound()
    {
        var error = Assert.Throws<MediaException>(() => _factory.Open(Path.Combine(_video.Directory, "missing.mp4")));

        Assert.Equal(MediaErrorKind.FileNotFound, error.Kind);
    }

    [Fact]
    public void Corrupt_file_is_reported_as_UnsupportedFormat_not_a_native_crash()
    {
        var error = Assert.Throws<MediaException>(() => _factory.Open(_video.CorruptFilePath));

        Assert.Equal(MediaErrorKind.UnsupportedFormat, error.Kind);
    }

    [Fact]
    public void Operations_after_dispose_throw_ObjectDisposedException()
    {
        var decoder = _factory.Open(_video.FilePath);
        decoder.Dispose();
        decoder.Dispose(); // twice is fine

        Assert.Throws<ObjectDisposedException>(() => decoder.ReadNext());
        Assert.Throws<ObjectDisposedException>(() => decoder.Seek(1));
    }

    [Fact]
    public async Task Playback_session_plays_a_real_video_to_the_end_and_releases_native_frames()
    {
        var liveBefore = OwnedVideoFrame.LiveInstances;
        var sink = new DisposingSink();
        var decoder = _factory.Open(_video.FilePath);
        var session = PlaybackSession.Start(decoder, sink, TimeProvider.System);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, e) =>
        {
            if (e.Current is PlaybackState.Ended or PlaybackState.Faulted)
            {
                ended.TrySetResult();
            }
        };

        session.Play();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(PlaybackState.Ended, session.State);
        // NOTE: playback never drops the last frame (since slice 2), so this range is looser than it needs to be
        Assert.InRange(sink.LastFrameIndex, SyntheticVideo.FrameCount - 5, SyntheticVideo.FrameCount - 1);
        await session.DisposeAsync();
        Assert.Equal(liveBefore, OwnedVideoFrame.LiveInstances);
    }

    private static void AssertGray(OwnedVideoFrame frame, int expected)
    {
        var pixels = frame.GetPixels();
        var center = ((frame.Height / 2) * frame.Stride) + ((frame.Width / 2) * 3);
        for (var channel = 0; channel < 3; channel++)
        {
            Assert.InRange(pixels[center + channel], Math.Max(0, expected - JpegTolerance), Math.Min(255, expected + JpegTolerance));
        }
    }

    // stands in for the WPF presenter: remembers the last frame and disposes it
    private sealed class DisposingSink : IPreviewFrameSink
    {
        private long _last = -1;

        public long LastFrameIndex => Interlocked.Read(ref _last);

        public void Present(OwnedVideoFrame frame)
        {
            Interlocked.Exchange(ref _last, frame.FrameIndex);
            frame.Dispose();
        }

        public void Clear()
        {
        }
    }
}
