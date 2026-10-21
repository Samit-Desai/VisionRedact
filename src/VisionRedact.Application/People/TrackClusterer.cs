using VisionRedact.Domain.Detections;
using VisionRedact.Domain.People;

namespace VisionRedact.Application.People;

// the rules the grouping must obey, whatever the fingerprints say:
// - two faces on one frame are never one person (nobody stands in two places at once).
//   except two boxes that overlap a lot: that's one face reported twice (two models, or a track that split)
// - your choices: tracks confirmed for one person = must-link, for different people = cannot-link
// a fact about the video beats a guess from the AI, so look-alike strangers side by side can never be merged
public sealed record LinkConstraints(IReadOnlySet<(int A, int B)> CannotLink, IReadOnlyList<IReadOnlyCollection<int>> MustLink)
{
    public static LinkConstraints Build(IEnumerable<Detection> detections, PeopleIndex people, double duplicateOverlap)
    {
        var cannot = new HashSet<(int, int)>();

        // same frame, different tracks, not a duplicate -> cannot-link
        foreach (var frame in detections.Where(d => d.TrackId is not null).GroupBy(d => d.FrameIndex))
        {
            var faces = frame.ToArray();
            for (var i = 0; i < faces.Length; i++)
            {
                for (var j = i + 1; j < faces.Length; j++)
                {
                    if (faces[i].TrackId != faces[j].TrackId && faces[i].Box.IntersectionOverUnion(faces[j].Box) < duplicateOverlap)
                    {
                        cannot.Add(Pair(faces[i].TrackId!.Value, faces[j].TrackId!.Value));
                    }
                }
            }
        }

        // confirmed tracks of one person -> one must-link group. confirmed tracks of two people -> cannot-link
        var confirmed = people.Assignments.Where(a => a.IsConfirmed)
            .GroupBy(a => a.PersonId)
            .Select(g => (IReadOnlyCollection<int>)g.Select(a => a.TrackId).ToArray())
            .ToArray();
        for (var i = 0; i < confirmed.Length; i++)
        {
            for (var j = i + 1; j < confirmed.Length; j++)
            {
                foreach (var a in confirmed[i])
                {
                    foreach (var b in confirmed[j])
                    {
                        cannot.Add(Pair(a, b));
                    }
                }
            }
        }

        return new LinkConstraints(cannot, confirmed);
    }

    public bool Forbids(int a, int b) => CannotLink.Contains(Pair(a, b));

    // smaller id first, so (7, 3) and (3, 7) are one key
    private static (int, int) Pair(int a, int b) => a < b ? (a, b) : (b, a);
}

// groups tracks into people: average-linkage clustering under the rules above.
// each track starts alone (confirmed ones start together). then join the two most alike groups that no rule forbids,
// again and again, until no allowed pair reaches the threshold. no need to know how many people there are,
// that's why this and not k-means. average = over all track pairs, so one odd-looking track can't drag a group along
public static class TrackClusterer
{
    public static IReadOnlyList<IReadOnlyCollection<int>> Cluster(IReadOnlyDictionary<int, FaceEmbedding> tracks, LinkConstraints constraints, float threshold)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(constraints);

        // start: each confirmed person's tracks in one group, every other track alone. only tracks w/ a fingerprint:
        // the rest (tiny faces, no landmarks, all rejected) aren't grouped and stay unidentified.
        // TODO: the People tab can't assign those by hand yet, ok for now
        var groups = new List<List<int>>();
        var placed = new HashSet<int>();
        foreach (var group in constraints.MustLink)
        {
            var members = group.Where(tracks.ContainsKey).ToList();
            if (members.Count > 0)
            {
                groups.Add(members);
                placed.UnionWith(members);
            }
        }

        groups.AddRange(tracks.Keys.Where(t => !placed.Contains(t)).Order().Select(t => new List<int> { t }));

        // group-to-group average similarity, and which pairs a rule forbids
        var n = groups.Count;
        var similarity = new double[n, n];
        var forbidden = new bool[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                similarity[i, j] = similarity[j, i] = Average(groups[i], groups[j], tracks);
                forbidden[i, j] = forbidden[j, i] = groups[i].Any(a => groups[j].Any(b => constraints.Forbids(a, b)));
            }
        }

        // join the most alike allowed pair, until none reaches the threshold
        var alive = Enumerable.Repeat(true, n).ToArray();
        while (true)
        {
            var (bestI, bestJ, best) = (-1, -1, double.NegativeInfinity);
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; alive[i] && j < n; j++)
                {
                    if (alive[j] && !forbidden[i, j] && similarity[i, j] > best)
                    {
                        (bestI, bestJ, best) = (i, j, similarity[i, j]);
                    }
                }
            }

            if (bestI < 0 || best < threshold)
            {
                break;
            }

            // Lance-Williams: the merged group's average to group k = the size-weighted mean of the two old ones.
            // same as averaging all the pairs again, w/o redoing them
            var (sizeI, sizeJ) = (groups[bestI].Count, groups[bestJ].Count);
            for (var k = 0; k < n; k++)
            {
                if (alive[k] && k != bestI && k != bestJ)
                {
                    similarity[bestI, k] = similarity[k, bestI] = ((sizeI * similarity[bestI, k]) + (sizeJ * similarity[bestJ, k])) / (sizeI + sizeJ);
                    // forbidden w/ either half = forbidden w/ the merged group. e.g. T3 and T7 shared a frame:
                    // once T3 and T41 are one group, T7 can't get in through T41 either
                    forbidden[bestI, k] = forbidden[k, bestI] = forbidden[bestI, k] || forbidden[bestJ, k];
                }
            }

            groups[bestI].AddRange(groups[bestJ]);
            alive[bestJ] = false;
        }

        return groups.Where((_, i) => alive[i]).Select(g => (IReadOnlyCollection<int>)g.Order().ToArray()).ToArray();
    }

    private static double Average(List<int> a, List<int> b, IReadOnlyDictionary<int, FaceEmbedding> tracks)
    {
        var sum = 0.0;
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                sum += tracks[x].CosineSimilarity(tracks[y]);
            }
        }

        return sum / (a.Count * b.Count);
    }
}
