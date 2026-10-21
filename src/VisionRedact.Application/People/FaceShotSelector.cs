using VisionRedact.Domain.Detections;

namespace VisionRedact.Application.People;

// picks a few good faces per track to fingerprint, from the stored boxes alone, no pixels.
// cuts each track into ShotsPerTrack equal parts in time and takes the best box of each (area * confidence), so the
// shots are different moments (angle, light), not three almost identical frames in a row.
// skips Rejected boxes (false boxes, or faces someone chose to keep visible) and faces under MinFacePixels
public static class FaceShotSelector
{
    public static IReadOnlyList<FaceShot> Select(IEnumerable<Detection> detections, int frameWidth, int frameHeight, IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(detections);
        ArgumentNullException.ThrowIfNull(options);

        var shots = new List<FaceShot>();
        var usable = detections.Where(d => d is { Class: ObjectClass.Face, TrackId: not null } && d.Status != ReviewStatus.Rejected
                                           && d.Box.Height * frameHeight >= options.MinFacePixels);
        foreach (var track in usable.GroupBy(d => d.TrackId!.Value))
        {
            var ordered = track.OrderBy(d => d.FrameIndex).ToArray();
            // fewer usable boxes than ShotsPerTrack -> one shot per box
            var parts = Math.Min(options.ShotsPerTrack, ordered.Length);
            for (var p = 0; p < parts; p++)
            {
                // part p = [len * p / parts, len * (p + 1) / parts)
                var segment = ordered.AsSpan((ordered.Length * p) / parts, ((ordered.Length * (p + 1)) / parts) - ((ordered.Length * p) / parts));
                var best = segment[0];
                foreach (var d in segment)
                {
                    best = Score(d, frameWidth, frameHeight) > Score(best, frameWidth, frameHeight) ? d : best;
                }

                shots.Add(new FaceShot(track.Key, best.FrameIndex, best.Box));
            }
        }

        return shots;
    }

    private static double Score(Detection d, int width, int height) => d.Box.Width * width * d.Box.Height * height * d.Confidence;
}
