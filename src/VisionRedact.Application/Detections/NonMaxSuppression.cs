namespace VisionRedact.Application.Detections;

// greedy NMS: keep the best box, drop the same-class boxes that overlap it too much, repeat.
// models report one face several times, slightly shifted. NmsDecoder wraps the UltraFace and classic YOLO decoders
// with this. NMS-free models (YOLO26) skip it (ADR-0021). O(n^2), but n is small after the confidence filter
public static class NonMaxSuppression
{
    // best first, max maxResults
    public static IReadOnlyList<DetectionCandidate> Apply(IReadOnlyList<DetectionCandidate> candidates, float iouThreshold, int maxResults = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count <= 1)
        {
            return candidates.ToArray();
        }

        // sort indexes, so the caller's list stays as it is
        var order = new int[candidates.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // ties -> lower index first, so the result is always the same
        Array.Sort(order, (a, b) =>
        {
            var byScore = candidates[b].Confidence.CompareTo(candidates[a].Confidence);
            return byScore != 0 ? byScore : a.CompareTo(b);
        });

        // take the best box still alive, then knock out every weaker box of the same class that overlaps it
        // too much (the same object twice)
        var suppressed = new bool[candidates.Count];
        var kept = new List<DetectionCandidate>();
        for (var i = 0; i < order.Length && kept.Count < maxResults; i++)
        {
            var current = order[i];
            if (suppressed[current])
            {
                continue;
            }

            var box = candidates[current];
            kept.Add(box);
            for (var j = i + 1; j < order.Length; j++)
            {
                var other = order[j];
                if (!suppressed[other]
                    && candidates[other].Class == box.Class
                    && box.Box.IntersectionOverUnion(candidates[other].Box) > iouThreshold)
                {
                    suppressed[other] = true;
                }
            }
        }

        return kept;
    }
}
