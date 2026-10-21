using VisionRedact.Domain.Detections;

namespace VisionRedact.Application.Tracking;

// gives each box a TrackId, so the same face on the next scanned frame gets the same number.
// TrackId -> track review in the panel, box sliding between scans (GetForDisplay), people grouping.
// rule: only set TrackId, never touch a box or a confidence (ADR-0016).
// the scan's AI task calls Assign once per scanned frame, in order, on one thread
public interface IObjectTracker
{
    IReadOnlyList<Detection> Assign(long frameIndex, IReadOnlyList<Detection> detections);

    // forget all tracks, number new ones from firstTrackId. AnalysisJob passes HighestTrackId + 1,
    // so ids never repeat in one video (ADR-0024)
    void Reset(int firstTrackId = 1);
}
