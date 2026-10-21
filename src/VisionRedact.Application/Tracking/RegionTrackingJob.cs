using Microsoft.Extensions.Logging;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.Redaction;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.Redaction;

namespace VisionRedact.Application.Tracking;

// follows a box by its pixels, no AI. for hand-drawn regions (IObjectTracker links AI boxes instead).
// EmguVisualTracker w/ KCF by default, b/c CSRT fails in Emgu CV 4.13 (ADR-0016)
public interface IVisualTracker : IDisposable
{
    void Initialize(OwnedVideoFrame frame, NormalizedRect box);

    // false = target lost
    bool TryUpdate(OwnedVideoFrame frame, out NormalizedRect box);
}

// a fresh one per job, trackers hold state and native memory
public interface IVisualTrackerFactory
{
    IVisualTracker Create();
}

// "Track" on a drawn region: reads its frames in order, the visual tracker moves the box, the path goes on the region.
// TrackCommand (RedactionViewModel) -> this job -> IRedactionRegionStore.Update -> BoxAt in preview + export.
// the Path is saved w/ the project. own decoder like the scan and export, one frame alive at a time
public sealed class RegionTrackingJob : ProcessingJob
{
    private readonly string _videoPath;
    private readonly IVideoDecoderFactory _decoders;
    private readonly IVisualTrackerFactory _trackers;
    private readonly IRedactionRegionStore _store;
    private readonly Guid _regionId;

    public RegionTrackingJob(string videoPath, IVideoDecoderFactory decoders, IVisualTrackerFactory trackers, IRedactionRegionStore store, Guid regionId, ILogger<RegionTrackingJob>? logger = null)
        : base("Track region", logger: logger)
    {
        _videoPath = videoPath;
        _decoders = decoders;
        _trackers = trackers;
        _store = store;
        _regionId = regionId;
    }

    // last frame the target was found on. -1 until the job ends (and if nothing got tracked)
    public long LastTrackedFrame { get; private set; } = -1;

    // stopped early b/c the tracker lost the object
    public bool TargetLost { get; private set; }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // the user may have deleted the region since the click
        var region = _store.Regions.FirstOrDefault(r => r.Id == _regionId)
            ?? throw new InvalidOperationException("The region no longer exists.");

        var path = new List<TrackPoint>();
        try
        {
            using var decoder = _decoders.Open(_videoPath);
            using var tracker = _trackers.Create();
            decoder.Seek(region.StartFrame);
            var total = region.FrameCount;

            // seed the tracker w/ the drawn box on the region's first frame
            using (var first = decoder.ReadNext() ?? throw new MediaException(MediaErrorKind.DecodeFailed, "The region's first frame could not be decoded.", _videoPath))
            {
                tracker.Initialize(first, region.Box);
                path.Add(new TrackPoint(first.FrameIndex, region.Box));
            }

            // stop at the end of the range, at the end of the stream, or when the target is lost
            while (decoder.Position <= region.EndFrame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var frame = decoder.ReadNext();
                if (frame is null)
                {
                    break;
                }

                if (!tracker.TryUpdate(frame, out var box) || box.IsEmpty)
                {
                    TargetLost = true;
                    break;
                }

                path.Add(new TrackPoint(frame.FrameIndex, box));
                ReportProgress(path.Count, total, $"Tracked {path.Count:N0} of {total:N0} frames");
            }
        }
        finally
        {
            // keep what we got, also on cancel or failure: the path so far is still right.
            // re-read the region, it may have changed meanwhile. trim EndFrame only when the target was lost,
            // so we never keep a box that covers nothing
            if (path.Count > 0)
            {
                LastTrackedFrame = path[^1].Frame;
                var current = _store.Regions.FirstOrDefault(r => r.Id == _regionId);
                if (current is not null)
                {
                    _store.Update(current with { Path = path, EndFrame = TargetLost ? LastTrackedFrame : current.EndFrame });
                }
            }
        }

        return Task.CompletedTask;
    }
}
