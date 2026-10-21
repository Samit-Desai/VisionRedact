using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.Redaction;
using VisionRedact.Application.Tests.Fakes;
using VisionRedact.Application.Tracking;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.Jobs;
using VisionRedact.Domain.Redaction;

namespace VisionRedact.Application.Tests.Tracking;

public class IouObjectTrackerTests
{
    private static Detection Face(long frame, double x, ObjectClass c = ObjectClass.Face) =>
        Detection.Create(frame, new NormalizedRect(x, 0.4, 0.1, 0.1), 0.9f, c);

    [Fact]
    public void A_moving_face_keeps_one_track_id()
    {
        var tracker = new IouObjectTracker();

        var ids = Enumerable.Range(0, 20)
            .Select(f => tracker.Assign(f, [Face(f, 0.1 + (f * 0.01))]).Single().TrackId)
            .Distinct()
            .ToArray();

        Assert.Single(ids);
    }

    [Fact]
    public void Reset_numbers_new_tracks_from_the_given_first_id()
    {
        var tracker = new IouObjectTracker();
        tracker.Assign(0, [Face(0, 0.1)]);

        tracker.Reset(firstTrackId: 42);
        var ids = tracker.Assign(1, [Face(1, 0.1), Face(1, 0.6)]).Select(d => d.TrackId).ToArray();

        Assert.Equal([42, 43], ids);
    }

    [Fact]
    public void Two_faces_get_separate_tracks_and_keep_them()
    {
        var tracker = new IouObjectTracker();

        var first = tracker.Assign(0, [Face(0, 0.1), Face(0, 0.7)]);
        var second = tracker.Assign(1, [Face(1, 0.71), Face(1, 0.11)]); // order swapped on purpose

        Assert.NotEqual(first[0].TrackId, first[1].TrackId);
        Assert.Equal(first[0].TrackId, second[1].TrackId);
        Assert.Equal(first[1].TrackId, second[0].TrackId);
    }

    [Fact]
    public void Track_survives_a_short_gap_but_not_a_long_one()
    {
        var tracker = new IouObjectTracker(maxMissedAnalyses: 2);
        var id = tracker.Assign(0, [Face(0, 0.3)])[0].TrackId;

        tracker.Assign(1, []);
        tracker.Assign(2, []);
        Assert.Equal(id, tracker.Assign(3, [Face(3, 0.3)])[0].TrackId);   // 2 missed: kept

        tracker.Assign(4, []);
        tracker.Assign(5, []);
        tracker.Assign(6, []);
        Assert.NotEqual(id, tracker.Assign(7, [Face(7, 0.3)])[0].TrackId); // 3 missed: new track
    }

    [Fact]
    public void A_small_fast_face_sampled_sparsely_keeps_its_track()
    {
        // a ~30 px face in 1080p moving ~0.8 of its width per sample (every 12th frame). the boxes barely overlap, so
        // IoU alone breaks the track at each sample and there is nothing to interpolate to.
        // the distance fallback fixed that (Performance.md, optimization 5)
        var tracker = new IouObjectTracker();
        const double size = 0.016;
        var ids = Enumerable.Range(0, 10)
            .Select(i => tracker.Assign(i * 12, [Detection.Create(i * 12, new NormalizedRect(0.2 + (i * 0.013), 0.3, size, size * 1.8), 0.8f, ObjectClass.Face)]).Single().TrackId)
            .Distinct()
            .ToArray();

        Assert.Single(ids);
    }

    [Fact]
    public void Distance_fallback_does_not_join_far_apart_or_very_different_boxes()
    {
        var tracker = new IouObjectTracker();
        var id = tracker.Assign(0, [Detection.Create(0, new NormalizedRect(0.2, 0.3, 0.02, 0.02), 0.8f, ObjectClass.Face)])[0].TrackId;

        var far = tracker.Assign(12, [Detection.Create(12, new NormalizedRect(0.4, 0.3, 0.02, 0.02), 0.8f, ObjectClass.Face)])[0].TrackId;
        var huge = tracker.Assign(24, [Detection.Create(24, new NormalizedRect(0.2, 0.3, 0.1, 0.1), 0.8f, ObjectClass.Face)])[0].TrackId;

        Assert.NotEqual(id, far);   // 10 box sizes away
        Assert.NotEqual(id, huge);  // 25x the area
    }

    [Fact]
    public void Different_classes_never_share_a_track()
    {
        var tracker = new IouObjectTracker();
        var face = tracker.Assign(0, [Face(0, 0.3)])[0].TrackId;

        var person = tracker.Assign(1, [Face(1, 0.3, ObjectClass.Person)])[0].TrackId;

        Assert.NotEqual(face, person);
    }

    [Fact]
    public void Tracks_aggregate_into_DetectionTrack_summaries()
    {
        var tracker = new IouObjectTracker();
        var all = Enumerable.Range(0, 10).SelectMany(f => tracker.Assign(f, [Face(f, 0.2), Face(f, 0.8)])).ToList();
        all[3] = all[3] with { Status = ReviewStatus.Rejected };

        var tracks = DetectionTrack.FromDetections(all);

        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t => Assert.Equal((0L, 9L, 10), (t.FirstFrame, t.LastFrame, t.DetectionCount)));
        Assert.Equal(1, tracks.Sum(t => t.RejectedCount));
    }
}

public class RegionTrackingJobTests
{
    [Fact]
    public async Task Tracked_path_is_stored_on_the_region_and_used_for_redaction()
    {
        var store = new RedactionRegionStore();
        var region = RedactionRegion.Create(new NormalizedRect(0.1, 0.1, 0.2, 0.2), 10, 30, RedactionEffectKind.Pixelate);
        store.Add(region);
        var job = new RegionTrackingJob("v.mp4", new FakeVideoDecoderFactory(_ => new FakeVideoDecoder(frameCount: 100)), new DriftingTrackerFactory(), store, region.Id);

        await Run(job);

        var tracked = store.Regions.Single();
        Assert.Equal(ProcessingJobState.Completed, job.State);
        Assert.Equal(21, tracked.Path!.Count);                                 // frames 10..30
        Assert.Equal(0.1 + (20 * 0.01), tracked.BoxAt(30).X, 6);               // followed the drift
        Assert.Equal(tracked.BoxAt(30), tracked.BoxAt(31));                    // holds the last position
        var plan = RedactionPlanner.Plan(30, 1000, 1000, [], store.Regions, RedactionSettings.Default);
        Assert.Equal(300, Assert.Single(plan).Roi.X);                          // planner uses the tracked box
    }

    [Fact]
    public async Task Lost_target_trims_the_region_at_the_last_tracked_frame()
    {
        var store = new RedactionRegionStore();
        var region = RedactionRegion.Create(new NormalizedRect(0.1, 0.1, 0.2, 0.2), 0, 50, RedactionEffectKind.SolidBlack);
        store.Add(region);
        var job = new RegionTrackingJob("v.mp4", new FakeVideoDecoderFactory(_ => new FakeVideoDecoder(frameCount: 100)), new DriftingTrackerFactory(loseAfter: 5), store, region.Id);

        await Run(job);

        Assert.True(job.TargetLost);
        Assert.Equal(5, store.Regions.Single().EndFrame);
    }

    private static async Task Run(ProcessingJob job)
    {
        new ProcessingJobManager().Start(job);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // stands in for the OpenCV pixel tracker: moves the box right a bit each frame, can "lose" it after N updates
    private sealed class DriftingTrackerFactory(int loseAfter = int.MaxValue) : IVisualTrackerFactory
    {
        public IVisualTracker Create() => new Drifting(loseAfter);

        private sealed class Drifting(int loseAfter) : IVisualTracker
        {
            private NormalizedRect _box;
            private int _updates;

            public void Initialize(OwnedVideoFrame frame, NormalizedRect box) => _box = box;

            public bool TryUpdate(OwnedVideoFrame frame, out NormalizedRect box)
            {
                _box = _box.Offset(0.01, 0);
                box = _box;
                return ++_updates <= loseAfter;
            }

            public void Dispose()
            {
            }
        }
    }
}
