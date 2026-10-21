using System.Windows;

namespace VisionRedact.Presentation.Wpf.Controls;

// view models build lists of these, VideoTimelineControl / TimelineTrack only read and draw them

// also picks the color (TimelineTrack)
public enum TimelineMarkerKind
{
    Detection = 0,
    Approved = 1,
    Rejected = 2,
}

// a frame worth marking, e.g. one w/ detections. a small struct, so a long list stays cheap
public readonly record struct TimelineMarker(long Frame, TimelineMarkerKind Kind);

public enum TimelineRangeKind
{
    Redaction = 0,
    SelectedRedaction = 1,
    Analyzed = 2,
}

// inclusive frames, e.g. a manual region's lifetime
public readonly record struct TimelineRange(long Start, long End, TimelineRangeKind Kind);

// for VideoTimelineControl.SeekRequested (bubbles up). nothing handles it today, see the control
public sealed class TimelineSeekEventArgs(RoutedEvent routedEvent, object source, long frame) : RoutedEventArgs(routedEvent, source)
{
    public long Frame { get; } = frame;
}

public delegate void TimelineSeekEventHandler(object sender, TimelineSeekEventArgs e);
