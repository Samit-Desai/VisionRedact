using VisionRedact.Application.Media;
using VisionRedact.Domain.Detections;

namespace VisionRedact.Application.Detections;

// "Detect this frame" (player and Detections panel): a quick check of one frame, no full scan.
// own decoder, so the player's position and frames are never touched.
// no tracker -> no TrackId, so these boxes don't slide between frames and never get grouped into people
public sealed class FrameDetectionService
{
    private readonly IVideoDecoderFactory _decoderFactory;
    private readonly DetectorHost _detectorHost;
    private readonly IDetectionRepository _repository;

    public FrameDetectionService(IVideoDecoderFactory decoderFactory, DetectorHost detectorHost, IDetectionRepository repository)
    {
        _decoderFactory = decoderFactory;
        _detectorHost = detectorHost;
        _repository = repository;
    }

    public async Task<IReadOnlyList<Detection>> DetectFrameAsync(
        string videoPath,
        long frameIndex,
        IReadOnlyList<DetectionModel> models,
        CancellationToken cancellationToken = default)
    {
        // the using only disposes the borrowed handle, the model stays loaded
        using var detector = await _detectorHost.GetAsync(models, cancellationToken).ConfigureAwait(false);

        // decode + inference are blocking native work: one Task.Run here
        var detections = await Task.Run(
            () =>
            {
                using var decoder = _decoderFactory.Open(videoPath);
                decoder.Seek(frameIndex);
                using var frame = decoder.ReadNext()
                    ?? throw new MediaException(MediaErrorKind.DecodeFailed, $"Frame {frameIndex} could not be decoded.", videoPath);
                cancellationToken.ThrowIfCancellationRequested();
                return detector.Detect(frame, cancellationToken)
                    .Select(c => Detection.Create(frame.FrameIndex, c.Box, c.Confidence, c.Class))
                    .ToArray();
            },
            cancellationToken).ConfigureAwait(false);

        // stored like a scanned frame: replaces the unreviewed boxes on it, keeps reviewed ones, marks it analyzed
        _repository.AddFrameResults(frameIndex, detections);
        return detections;
    }
}
