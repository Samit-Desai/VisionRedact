using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.People;
using VisionRedact.Application.Playback;
using VisionRedact.Application.Redaction;

namespace VisionRedact.Application.Workspace;

// the open video: its playback session and frame reader. opening another one cancels the old jobs and clears
// the detections, regions and people.
// ShellViewModel and ProjectService call OpenAsync, ShellViewModel.ShutdownAsync calls DisposeAsync.
// the new decoder opens BEFORE the old video closes, so a broken file leaves the current video alone
// (the price: two decoders for a moment).
// ConfigureAwait(false) everywhere, nothing here needs the UI thread. the caller's own await comes back to it
public sealed class VideoWorkspace : IAsyncDisposable
{
    private readonly IVideoDecoderFactory _decoderFactory;
    private readonly IPreviewFrameSink _previewSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PlaybackOptions _playbackOptions;
    private readonly IPreviewFrameProcessor? _previewProcessor;
    private readonly IDetectionRepository? _detections;
    private readonly IRedactionRegionStore? _regions;
    private readonly IProcessingJobManager? _jobs;
    private readonly IPeopleStore? _people;
    private readonly ILogger _logger;
    // set on a pool thread (after ConfigureAwait(false)), read by UI code. CloseCurrentAsync swaps it out atomically
    private volatile PlaybackSession? _currentSession;

    // previews and thumbnails. replaced and disposed w/ the session
    private volatile VideoFrameReader? _frameReader;

    // 1 = an open or close is running
    private int _operationInProgress;

    public VideoWorkspace(
        IVideoDecoderFactory decoderFactory,
        IPreviewFrameSink previewSink,
        TimeProvider timeProvider,
        ILoggerFactory? loggerFactory = null,
        PlaybackOptions? playbackOptions = null,
        IPreviewFrameProcessor? previewProcessor = null,
        IDetectionRepository? detections = null,
        IRedactionRegionStore? regions = null,
        IProcessingJobManager? jobs = null,
        IPeopleStore? people = null)
    {
        ArgumentNullException.ThrowIfNull(decoderFactory);
        ArgumentNullException.ThrowIfNull(previewSink);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _decoderFactory = decoderFactory;
        _previewSink = previewSink;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<VideoWorkspace>();
        _playbackOptions = playbackOptions ?? PlaybackOptions.Default;
        _previewProcessor = previewProcessor;
        _detections = detections;
        _regions = regions;
        _jobs = jobs;
        _people = people;
    }

    public PlaybackSession? CurrentSession => _currentSession;

    // null w/o a video
    public VideoFrameReader? FrameReader => _frameReader;

    // new decoder -> close the old video -> cancel old jobs -> reset per-video data -> new session + frame reader.
    // throws MediaException if the file isn't a usable video. canceled: a decoder opened meanwhile is disposed
    public async Task<PlaybackSession> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnterOperation();
        try
        {
            // blocking native work (probe, codec init), so Task.Run, here at the boundary and not inside the decoder
            var decoder = await Task.Run(() => _decoderFactory.Open(path), cancellationToken).ConfigureAwait(false);

            // the decoder is ours until PlaybackSession.Start takes it
            if (cancellationToken.IsCancellationRequested)
            {
                decoder.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            // the new file opened fine, so now it's safe to close the old one
            await CloseCurrentAsync().ConfigureAwait(false);

            // stop the old video's jobs before their data is thrown away
            if (_jobs is not null)
            {
                await _jobs.CancelAllAsync().ConfigureAwait(false);
            }

            // forget the old detections, regions and people; size the detection store for the new video
            _detections?.Reset(decoder.Metadata.FrameCount);
            _regions?.Clear();
            _people?.Reset();

            // the session owns the decoder from here
            var session = PlaybackSession.Start(decoder, _previewSink, _timeProvider, _loggerFactory.CreateLogger<PlaybackSession>(), _playbackOptions, _previewProcessor);
            _currentSession = session;
            _frameReader = new VideoFrameReader(_decoderFactory, path);

            var m = decoder.Metadata;
            _logger.LogInformation(
                "Opened {Path}: {Width}x{Height} @ {Fps:F3} fps, {Frames} frames, codec {Codec}, backend {Backend}.",
                m.SourcePath, m.Width, m.Height, m.FramesPerSecond, m.FrameCount, m.Codec, decoder.BackendName);
            return session;
        }
        finally
        {
            ExitOperation();
        }
    }

    // same guard as open.
    // TODO: only ProjectPersistenceTests calls this, the app closes through DisposeAsync. use it or drop it, ok for now
    public async Task CloseAsync()
    {
        EnterOperation();
        try
        {
            await CloseCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    public async ValueTask DisposeAsync() => await CloseCurrentAsync().ConfigureAwait(false);

    // stop + dispose the session and the frame reader, then free a frame still waiting in the presenter
    private async Task CloseCurrentAsync()
    {
        // take it and null the field in one step: if shutdown and CloseAsync race, only one gets the session
        var session = Interlocked.Exchange(ref _currentSession, null);
        if (session is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _frameReader, null) is { } reader)
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }

        // order matters: stop the session first (no more frames come), then clear the presenter's pending frame
        await session.DisposeAsync().ConfigureAwait(false);
        _previewSink.Clear();
        _logger.LogInformation("Closed {Path}.", session.Metadata.SourcePath);
    }

    // re-entry guard: a second open or close while one runs (e.g. Open video while a project is still opening)
    // throws instead of tangling the state
    private void EnterOperation()
    {
        // one atomic step, b/c the flag gets cleared on a pool thread (after ConfigureAwait(false)) and set on the
        // UI thread. a plain check-then-set could let two in
        if (Interlocked.Exchange(ref _operationInProgress, 1) != 0)
        {
            throw new InvalidOperationException("Another open/close operation is already in progress.");
        }
    }

    private void ExitOperation() => Volatile.Write(ref _operationInProgress, 0);
}
