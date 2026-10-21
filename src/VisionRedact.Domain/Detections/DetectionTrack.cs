namespace VisionRedact.Domain.Detections;

// per-track summary, rebuilt from the detections each time and never stored, so tracking can be re-run (ADR-0016)
public sealed record DetectionTrack(int Id, ObjectClass Class, long FirstFrame, long LastFrame, int DetectionCount, float MaxConfidence, int RejectedCount)
{
    public bool IsFullyRejected => RejectedCount == DetectionCount;

    // TODO: only TrackingTests calls this. use it in the app or drop it, ok for now
    public static IReadOnlyList<DetectionTrack> FromDetections(IEnumerable<Detection> detections) =>
        detections
            .Where(d => d.TrackId is not null)
            .GroupBy(d => d.TrackId!.Value)
            .Select(g => new DetectionTrack(
                g.Key,
                g.First().Class,
                g.Min(d => d.FrameIndex),
                g.Max(d => d.FrameIndex),
                g.Count(),
                g.Max(d => d.Confidence),
                g.Count(d => d.Status == ReviewStatus.Rejected)))
            .OrderBy(t => t.FirstFrame)
            .ToArray();
}
