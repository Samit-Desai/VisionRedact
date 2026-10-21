using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Application.Tracking;

// a new box joins the live track (same class) whose *predicted* box it overlaps most, else a similar-size track whose
// predicted center is close, else it starts a new track. best pairs first, each track and box used once.
// why: between two scans a small face moves half its width or more, so IoU w/ its old box falls under the threshold.
// IoU alone broke such faces into many tracks, and prediction alone wasn't enough. the distance fallback fixed most
// of it (docs/Performance.md, optimization 5). one thread, frame order, not thread-safe
public sealed class IouObjectTracker : IObjectTracker
{
    // how much the newest move counts in the speed, so one jumpy box can't throw the prediction off
    private const double VelocitySmoothing = 0.6;

    private readonly List<ActiveTrack> _active = []; // seen recently enough to still match
    private int _nextId = 1;

    // iouThreshold: min overlap of the predicted and the new box.
    // maxMissedAnalyses: scans in a row a track may go unmatched before it's closed (scans, not video frames).
    // maxCentreDistance: the fallback gate, center distance in the track's box size (max of w, h). 0 = IoU only
    public IouObjectTracker(double iouThreshold = 0.3, int maxMissedAnalyses = 3, double maxCentreDistance = 1.5)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iouThreshold);
        ArgumentOutOfRangeException.ThrowIfNegative(maxMissedAnalyses);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCentreDistance);
        IouThreshold = iouThreshold;
        MaxMissedAnalyses = maxMissedAnalyses;
        MaxCentreDistance = maxCentreDistance;
    }

    public double IouThreshold { get; }

    public int MaxMissedAnalyses { get; }

    public double MaxCentreDistance { get; }

    public int ActiveTrackCount => _active.Count;

    public IReadOnlyList<Detection> Assign(long frameIndex, IReadOnlyList<Detection> detections)
    {
        // score every (track, box) pair of the same class.
        // IoU matches score 1..2, distance-only ones 0..1 (closer = higher), so an IoU match always wins
        var pairs = new List<(double Score, int Track, int Detection)>();
        for (var t = 0; t < _active.Count; t++)
        {
            var track = _active[t];
            var predicted = track.PredictAt(frameIndex);
            for (var d = 0; d < detections.Count; d++)
            {
                if (track.Class != detections[d].Class)
                {
                    continue;
                }

                var box = detections[d].Box;
                var iou = predicted.IntersectionOverUnion(box);
                if (iou >= IouThreshold)
                {
                    pairs.Add((1 + iou, t, d));
                }
                else if (MaxCentreDistance > 0 && SimilarSize(predicted, box))
                {
                    // distance in box sizes, so it works for small and big faces alike
                    var size = Math.Max(predicted.Width, predicted.Height);
                    var distance = Distance(predicted.Center, box.Center) / Math.Max(size, 1e-6);
                    if (distance <= MaxCentreDistance)
                    {
                        pairs.Add((1 - (distance / MaxCentreDistance), t, d));
                    }
                }
            }
        }

        // greedy: best pair first, each track and each box used once
        pairs.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var trackUsed = new bool[_active.Count];
        var assigned = new int?[detections.Count];
        foreach (var (_, t, d) in pairs)
        {
            if (!trackUsed[t] && assigned[d] is null)
            {
                trackUsed[t] = true;
                assigned[d] = t;
            }
        }

        // matched -> extends its track (and updates the speed), unmatched -> a new track
        var result = new Detection[detections.Count];
        for (var d = 0; d < detections.Count; d++)
        {
            ActiveTrack track;
            if (assigned[d] is { } t)
            {
                track = _active[t];
                track.Update(frameIndex, detections[d].Box);
            }
            else
            {
                track = new ActiveTrack(_nextId++, detections[d].Class, frameIndex, detections[d].Box);
                _active.Add(track);
            }

            result[d] = detections[d] with { TrackId = track.Id };
        }

        // age the old tracks that got nothing, drop the stale ones.
        // new tracks were appended after trackUsed.Length - 1, so this loop never touches them
        for (var t = 0; t < trackUsed.Length; t++)
        {
            if (!trackUsed[t])
            {
                _active[t].Missed++;
            }
        }

        _active.RemoveAll(track => track.Missed > MaxMissedAnalyses);
        return result;
    }

    public void Reset(int firstTrackId = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstTrackId);
        _active.Clear();
        _nextId = firstTrackId;
    }

    // size gate for the fallback: area within 0.4x..2.5x, so e.g. a much bigger face right in front
    // isn't taken for this track
    private static bool SimilarSize(NormalizedRect a, NormalizedRect b)
    {
        if (a.IsEmpty || b.IsEmpty)
        {
            return false;
        }

        var ratio = b.Area / a.Area;
        return ratio is >= 0.4 and <= 2.5;
    }

    private static double Distance(PointD a, PointD b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    // one live track: last box, when it was last seen, its speed, and how many scans in a row it missed
    private sealed class ActiveTrack(int id, ObjectClass objectClass, long frame, NormalizedRect box)
    {
        // smoothed center speed, normalized units per video frame. a new track starts at 0
        private double _vx;
        private double _vy;

        public int Id { get; } = id;

        public ObjectClass Class { get; } = objectClass;

        public NormalizedRect Box { get; private set; } = box;

        public long LastFrame { get; private set; } = frame;

        public int Missed { get; set; }

        // last box moved by speed * frames since then
        public NormalizedRect PredictAt(long frameIndex)
        {
            var frames = frameIndex - LastFrame;
            return frames <= 0 ? Box : Box.Offset(_vx * frames, _vy * frames);
        }

        public void Update(long frameIndex, NormalizedRect box)
        {
            var frames = frameIndex - LastFrame;
            if (frames > 0)
            {
                // per frame, so divide by the gap (scans are many frames apart), then blend into the old estimate
                var vx = (box.Center.X - Box.Center.X) / frames;
                var vy = (box.Center.Y - Box.Center.Y) / frames;
                _vx = (VelocitySmoothing * vx) + ((1 - VelocitySmoothing) * _vx);
                _vy = (VelocitySmoothing * vy) + ((1 - VelocitySmoothing) * _vy);
            }

            Box = box;
            LastFrame = frameIndex;
            Missed = 0;
        }
    }
}
