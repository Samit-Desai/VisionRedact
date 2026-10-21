using Prism.Events;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Playback;
using VisionRedact.Domain.Detections;

namespace VisionRedact.Presentation.Wpf.Events;

// coarse events only, a few per video or one per click (ADR-0010). never per-frame data: each publish allocates and
// gets marshaled, so frames go through IPreviewFrameSink and boxes through the repository.
// ShellViewModel publishes after open; the player, the Video tab and the Detections tab subscribe
public sealed class VideoOpenedEvent : PubSubEvent<PlaybackSession>
{
}

// Detections list -> player. null = cleared
public sealed class SelectedDetectionChangedEvent : PubSubEvent<Detection?>
{
}

// "go to frame": the Detections, Redaction and People tabs -> player
public sealed class SeekRequestedEvent : PubSubEvent<long>
{
}

// a job ended (Completed, Cancelled or Failed). the shell publishes it for export, the Detections tab for the scan.
// TODO: nothing subscribes yet, ok for now
public sealed class JobCompletedEvent : PubSubEvent<IProcessingJob>
{
}
