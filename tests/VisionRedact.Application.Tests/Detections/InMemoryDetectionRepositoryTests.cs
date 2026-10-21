using VisionRedact.Application.Detections;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Application.Tests.Detections;

public class InMemoryDetectionRepositoryTests
{
    private readonly InMemoryDetectionRepository _repository = new();

    public InMemoryDetectionRepositoryTests()
    {
        _repository.Reset(10_000);
    }

    private static Detection Face(long frame, float confidence = 0.9f, double x = 0.1) =>
        Detection.Create(frame, new NormalizedRect(x, 0.1, 0.1, 0.1), confidence, ObjectClass.Face);

    [Fact]
    public void Frame_lookup_returns_only_that_frame()
    {
        _repository.AddFrameResults(5, [Face(5), Face(5, x: 0.5)]);
        _repository.AddFrameResults(6, [Face(6)]);

        Assert.Equal(2, _repository.GetForFrame(5).Count);
        Assert.Single(_repository.GetForFrame(6));
        Assert.Empty(_repository.GetForFrame(7));
    }

    [Fact]
    public void Display_lookup_holds_sampled_results_for_the_sampling_interval()
    {
        _repository.HoldFrames = 4; // every 5th frame analyzed
        _repository.AddFrameResults(100, [Face(100)]);
        _repository.AddFrameResults(105, []);

        Assert.Single(_repository.GetForDisplay(100));
        Assert.Single(_repository.GetForDisplay(104));   // held
        Assert.Empty(_repository.GetForDisplay(105));    // scanned, no faces: must NOT show frame 100's box
        Assert.Empty(_repository.GetForDisplay(111));    // beyond hold, not analyzed
    }

    [Fact]
    public void Display_lookup_interpolates_a_track_between_analyzed_frames()
    {
        _repository.HoldFrames = 9; // every 10th frame analyzed
        var first = Face(100, x: 0.10) with { TrackId = 7 };
        _repository.AddFrameResults(100, [first]);
        _repository.AddFrameResults(110, [Face(110, x: 0.30) with { TrackId = 7 }]);

        var midway = Assert.Single(_repository.GetForDisplay(105));
        Assert.Equal(0.20, midway.Box.X, 9);          // halfway: follows the face instead of standing still (Performance.md, optimization 5)
        Assert.Equal(first.Id, midway.Id);            // same detection (selection, review status stay attached)
        Assert.Equal(0.10, Assert.Single(_repository.GetForDisplay(100)).Box.X, 9);
    }

    [Fact]
    public void Display_lookup_holds_boxes_whose_track_is_not_on_the_next_analyzed_frame()
    {
        _repository.HoldFrames = 9;
        _repository.AddFrameResults(100, [Face(100, x: 0.10) with { TrackId = 7 }, Face(100, x: 0.60)]);
        _repository.AddFrameResults(110, [Face(110, x: 0.30) with { TrackId = 8 }]); // different track: no match

        var boxes = _repository.GetForDisplay(105).Select(d => d.Box.X).Order().ToArray();
        Assert.Equal([0.10, 0.60], boxes, new ToleranceComparer(1e-9)); // held unchanged: never less redaction than before
    }

    private sealed class ToleranceComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;

        public int GetHashCode(double obj) => 0;
    }

    [Fact]
    public void Re_analysis_replaces_pending_results_but_keeps_human_review()
    {
        var approved = Face(10, x: 0.2);
        var pending = Face(10, x: 0.6);
        _repository.AddFrameResults(10, [approved, pending]);
        _repository.UpdateStatus(approved.Id, ReviewStatus.Approved);

        var fresh = Face(10, x: 0.61);
        _repository.AddFrameResults(10, [fresh]);

        var ids = _repository.GetForFrame(10).Select(d => d.Id).ToHashSet();
        Assert.Contains(approved.Id, ids);
        Assert.Contains(fresh.Id, ids);
        Assert.DoesNotContain(pending.Id, ids);
    }

    [Fact]
    public void Status_updates_produce_new_immutable_instances_and_notify()
    {
        var d = Face(1);
        _repository.AddFrameResults(1, [d]);
        DetectionsChangedEventArgs? change = null;
        _repository.Changed += (_, e) => change = e;

        Assert.True(_repository.UpdateStatus(d.Id, ReviewStatus.Rejected));

        Assert.Equal(ReviewStatus.Pending, d.Status);                          // original untouched
        Assert.Equal(ReviewStatus.Rejected, _repository.Get(d.Id)!.Status);
        Assert.False(_repository.Get(d.Id)!.IsRedacted);
        Assert.Single(change!.Updated);
        Assert.False(_repository.UpdateStatus(d.Id, ReviewStatus.Rejected));    // no-op
    }

    [Fact]
    public void Track_level_review_updates_every_member()
    {
        _repository.AddFrameResults(1, [Face(1) with { TrackId = 7 }]);
        _repository.AddFrameResults(2, [Face(2) with { TrackId = 7 }, Face(2, x: 0.8) with { TrackId = 8 }]);

        var count = _repository.UpdateStatusWhere(d => d.TrackId == 7, ReviewStatus.Rejected);

        Assert.Equal(2, count);
        Assert.Equal(1, _repository.Snapshot().Count(d => d.Status == ReviewStatus.Pending));
    }

    [Fact]
    public void Snapshot_is_ordered_by_frame_then_confidence()
    {
        _repository.AddFrameResults(9, [Face(9, 0.5f)]);
        _repository.AddFrameResults(3, [Face(3, 0.7f), Face(3, 0.99f, x: 0.5)]);

        Assert.Equal([(3L, 0.99f), (3L, 0.7f), (9L, 0.5f)], _repository.Snapshot().Select(d => (d.FrameIndex, d.Confidence)));
    }

    [Fact]
    public void Analyzed_frames_are_tracked_compactly_including_empty_results()
    {
        _repository.AddFrameResults(0, []);
        _repository.AddFrameResults(63, []);
        _repository.AddFrameResults(64, [Face(64)]);
        _repository.AddFrameResults(64, []); // twice: counted once

        Assert.Equal(3, _repository.AnalyzedFrameCount);
        Assert.Equal([0L, 63L, 64L], _repository.AnalyzedFrames());
    }

    [Fact]
    [Trait("Category", "Timing")] // wall-clock limit: CI filters it out (shared runner too noisy), run it locally
    public void Handles_fifty_thousand_detections_with_fast_frame_lookup()
    {
        _repository.Reset(100_000);
        for (var frame = 0; frame < 25_000; frame++)
        {
            _repository.AddFrameResults(frame, [Face(frame), Face(frame, x: 0.6)]);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var frame = 0; frame < 25_000; frame++)
        {
            _ = _repository.GetForDisplay(frame);
        }

        stopwatch.Stop();
        Assert.Equal(50_000, _repository.Count);
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"25k lookups took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Concurrent_writers_and_readers_do_not_corrupt_state()
    {
        var writer = Task.Run(() =>
        {
            for (var frame = 0; frame < 2_000; frame++)
            {
                _repository.AddFrameResults(frame, [Face(frame)]);
            }
        });
        var reviewer = Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                foreach (var d in _repository.GetForFrame(i % 500))
                {
                    _repository.UpdateStatus(d.Id, ReviewStatus.Approved);
                }

                _ = _repository.Snapshot().Count;
            }
        });

        await Task.WhenAll(writer, reviewer);

        Assert.Equal(2_000, _repository.Count);
        Assert.Equal(2_000, _repository.AnalyzedFrameCount);
    }

    [Fact]
    public void Highest_track_id_is_a_high_water_mark_cleared_only_by_reset()
    {
        var tracked = Face(1) with { TrackId = 7 };
        _repository.AddFrameResults(1, [tracked, Face(1, x: 0.5) with { TrackId = 3 }]);

        Assert.Equal(7, _repository.HighestTrackId);
        _repository.Remove(tracked.Id);
        Assert.Equal(7, _repository.HighestTrackId); // removing never frees a number for reuse (ADR-0024)

        _repository.Reset(100);
        Assert.Equal(0, _repository.HighestTrackId);
    }

    [Fact]
    public void Load_recomputes_the_highest_track_id()
    {
        _repository.Load(100, [Face(4) with { TrackId = 12 }, Face(8)], [4, 8], holdFrames: 0);

        Assert.Equal(12, _repository.HighestTrackId);
    }

    [Fact]
    public void Load_restores_detections_coverage_and_hold()
    {
        var saved = new[] { Face(4) with { Status = ReviewStatus.Approved }, Face(8) };

        _repository.Load(100, saved, [4, 8], holdFrames: 2);

        Assert.Equal(2, _repository.Count);
        Assert.Single(_repository.GetForDisplay(6));
        Assert.Equal(ReviewStatus.Approved, _repository.GetForFrame(4)[0].Status);
    }

    [Fact]
    public void Coverage_is_empty_before_any_scan()
    {
        _repository.Reset(100);

        var coverage = _repository.GetCoverage();

        Assert.True(coverage.IsNone);
        Assert.Equal(0, coverage.CoveredFrames);
        Assert.Equal([new FrameRange(0, 99)], coverage.Gaps);
    }

    [Fact]
    public void Coverage_counts_the_frames_each_sample_holds()
    {
        // the export dialog used to say "every frame was scanned" w/ sampling on too
        _repository.Reset(20);
        _repository.HoldFrames = 4; // every 5th frame analyzed
        foreach (var frame in new long[] { 0, 5, 10, 15 })
        {
            _repository.AddFrameResults(frame, []); // analyzed with no faces still counts as covered
        }

        var coverage = _repository.GetCoverage();

        Assert.True(coverage.IsComplete);
        Assert.Empty(coverage.Gaps);
        Assert.Equal(4, coverage.ScannedFrames);           // covered is not scanned: 4 frames scanned, 16 held
        Assert.False(coverage.IsEveryFrameScanned);

        for (long frame = 0; frame < 20; frame++)
        {
            _repository.AddFrameResults(frame, []);         // "Every frame" sampling
        }

        Assert.True(_repository.GetCoverage().IsEveryFrameScanned);
    }

    [Fact]
    public void Coverage_reports_the_frames_a_cancelled_scan_never_reached()
    {
        _repository.Reset(100);
        _repository.HoldFrames = 4;
        for (long frame = 0; frame <= 45; frame += 5)
        {
            _repository.AddFrameResults(frame, []);
        }

        var coverage = _repository.GetCoverage();

        Assert.Equal(50, coverage.CoveredFrames);   // frames 0-49: frame 45 holds up to 49
        Assert.Equal(0.5, coverage.Fraction);
        Assert.Equal([new FrameRange(50, 99)], coverage.Gaps);
    }

    [Fact]
    public void Coverage_lists_the_first_gaps_in_order_and_counts_them_all()
    {
        _repository.Reset(100);
        foreach (var frame in new long[] { 10, 30, 50 })
        {
            _repository.AddFrameResults(frame, []); // HoldFrames 0: each sample covers only itself
        }

        var coverage = _repository.GetCoverage(maxListedGaps: 2);

        Assert.Equal(3, coverage.CoveredFrames);
        Assert.Equal([new FrameRange(0, 9), new FrameRange(11, 29)], coverage.Gaps);
        Assert.Equal(4, coverage.GapCount);         // 0-9, 11-29, 31-49, 51-99
    }
}
