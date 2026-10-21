using VisionRedact.Application.Media;
using VisionRedact.Application.Tests.Fakes;
using VisionRedact.Application.Workspace;

namespace VisionRedact.Application.Tests.Workspace;

public class VideoWorkspaceTests
{
    private readonly RecordingFrameSink _sink = new();

    [Fact]
    public async Task OpenAsync_starts_a_session_that_presents_the_first_frame()
    {
        var decoder = new FakeVideoDecoder();
        await using var workspace = CreateWorkspace(_ => decoder);

        var session = await workspace.OpenAsync("a.mp4");

        Assert.Same(session, workspace.CurrentSession);
        await _sink.WaitForFrameAsync(0);
    }

    [Fact]
    public async Task Opening_a_second_video_releases_the_first_decoder()
    {
        var first = new FakeVideoDecoder(path: "C:\\a.mp4");
        var second = new FakeVideoDecoder(path: "C:\\b.mp4");
        await using var workspace = CreateWorkspace(path => path == "a.mp4" ? first : second);

        await workspace.OpenAsync("a.mp4");
        await workspace.OpenAsync("b.mp4");

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.Equal("C:\\b.mp4", workspace.CurrentSession!.Metadata.SourcePath);
        Assert.Equal(1, _sink.ClearCalls); // stale frame from video A cannot be shown for video B
    }

    [Fact]
    public async Task Failed_open_keeps_the_current_video()
    {
        var first = new FakeVideoDecoder();
        await using var workspace = CreateWorkspace(path => path == "good.mp4"
            ? first
            : throw new MediaException(MediaErrorKind.UnsupportedFormat, "corrupt", path));

        var current = await workspace.OpenAsync("good.mp4");
        var error = await Assert.ThrowsAsync<MediaException>(() => workspace.OpenAsync("corrupt.mp4"));

        Assert.Equal(MediaErrorKind.UnsupportedFormat, error.Kind);
        Assert.Same(current, workspace.CurrentSession);
        Assert.False(first.IsDisposed);
    }

    [Fact]
    public async Task Cancellation_while_opening_disposes_the_decoder_that_was_opened()
    {
        using var cts = new CancellationTokenSource();
        var decoder = new FakeVideoDecoder();
        await using var workspace = CreateWorkspace(_ =>
        {
            cts.Cancel(); // user cancels while the (blocking) native open is in progress
            return decoder;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.OpenAsync("a.mp4", cts.Token));

        Assert.True(decoder.IsDisposed);
        Assert.Null(workspace.CurrentSession);
    }

    [Fact]
    public async Task Concurrent_open_is_rejected()
    {
        using var release = new ManualResetEventSlim();
        await using var workspace = CreateWorkspace(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(5));
            return new FakeVideoDecoder();
        });

        var first = workspace.OpenAsync("a.mp4");
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.OpenAsync("b.mp4"));

        release.Set();
        await first;
    }

    [Fact]
    public async Task DisposeAsync_releases_the_current_session()
    {
        var decoder = new FakeVideoDecoder();
        var workspace = CreateWorkspace(_ => decoder);
        await workspace.OpenAsync("a.mp4");

        await workspace.DisposeAsync();

        Assert.True(decoder.IsDisposed);
        Assert.Null(workspace.CurrentSession);
    }

    private VideoWorkspace CreateWorkspace(Func<string, IVideoDecoder> open) =>
        new(new FakeVideoDecoderFactory(open), _sink, TimeProvider.System);
}
