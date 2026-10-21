using VisionRedact.Application.Media;
using VisionRedact.Application.Redaction;
using VisionRedact.Application.Tests.Fakes;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.Redaction;

namespace VisionRedact.Application.Tests.Redaction;

public class RedactionPlannerTests
{
    private static readonly RedactionSettings NoPadding = new(RedactionEffectKind.Pixelate, Padding: 0);

    private static Detection Face(double x, ReviewStatus status = ReviewStatus.Pending) =>
        Detection.Create(10, new NormalizedRect(x, 0.5, 0.1, 0.1), 0.9f, ObjectClass.Face) with { Status = status };

    [Fact]
    public void Pending_and_approved_detections_are_redacted_rejected_are_not()
    {
        var plan = RedactionPlanner.Plan(10, 1000, 1000,
            [Face(0.1), Face(0.4, ReviewStatus.Approved), Face(0.7, ReviewStatus.Rejected)], [], NoPadding);

        Assert.Equal([new PixelRect(100, 500, 100, 100), new PixelRect(400, 500, 100, 100)], plan.Select(p => p.Roi));
        Assert.All(plan, p => Assert.Equal(RedactionEffectKind.Pixelate, p.Effect));
    }

    [Fact]
    public void Padding_grows_detection_boxes_but_not_past_the_frame()
    {
        var plan = RedactionPlanner.Plan(10, 1000, 1000, [Face(0.0)], [], new RedactionSettings(Padding: 0.5));

        Assert.Equal(new PixelRect(0, 450, 150, 200), Assert.Single(plan).Roi);
    }

    [Fact]
    public void Manual_regions_apply_only_inside_their_frame_range_with_their_own_effect()
    {
        var region = RedactionRegion.Create(new NormalizedRect(0.2, 0.2, 0.2, 0.2), 5, 15, RedactionEffectKind.SolidBlack);

        Assert.Empty(RedactionPlanner.Plan(4, 100, 100, [], [region], NoPadding));
        Assert.Equal(RedactionEffectKind.SolidBlack, Assert.Single(RedactionPlanner.Plan(5, 100, 100, [], [region], NoPadding)).Effect);
        Assert.Single(RedactionPlanner.Plan(15, 100, 100, [], [region], NoPadding));
        Assert.Empty(RedactionPlanner.Plan(16, 100, 100, [], [region], NoPadding));
    }

    [Fact]
    public void Degenerate_boxes_never_produce_instructions()
    {
        var empty = Detection.Create(1, NormalizedRect.Empty, 0.9f, ObjectClass.Face);
        var outside = Detection.Create(1, new NormalizedRect(1.5, 1.5, 0.2, 0.2), 0.9f, ObjectClass.Face);

        Assert.Empty(RedactionPlanner.Plan(1, 1920, 1080, [empty, outside], [], NoPadding));
    }

    [Fact]
    public void Region_frame_range_is_normalized_when_created_backwards()
    {
        var region = RedactionRegion.Create(new NormalizedRect(0, 0, 0.1, 0.1), 50, 10, RedactionEffectKind.GaussianBlur);

        Assert.Equal((10, 50), (region.StartFrame, region.EndFrame));
        Assert.Equal(41, region.FrameCount);
    }
}

public class RedactionProcessorTests
{
    [Fact]
    public void Dispatches_each_instruction_to_the_effect_for_its_kind_with_clamped_roi()
    {
        var blur = new RecordingEffect(RedactionEffectKind.GaussianBlur);
        var black = new RecordingEffect(RedactionEffectKind.SolidBlack);
        var processor = new RedactionProcessor([blur, black]);
        using var frame = new TestVideoFrame(0, TimeSpan.Zero); // 4x2

        processor.Apply(frame,
        [
            new RedactionInstruction(new PixelRect(0, 0, 2, 2), RedactionEffectKind.GaussianBlur),
            new RedactionInstruction(new PixelRect(3, 1, 10, 10), RedactionEffectKind.SolidBlack),
            new RedactionInstruction(new PixelRect(50, 50, 2, 2), RedactionEffectKind.SolidBlack), // outside: skipped
        ], 0.7);

        Assert.Equal([new PixelRect(0, 0, 2, 2)], blur.Calls);
        Assert.Equal([new PixelRect(3, 1, 1, 1)], black.Calls);
    }

    [Fact]
    public void Unknown_effect_is_an_error_not_a_silent_skip()
    {
        var processor = new RedactionProcessor([]);
        using var frame = new TestVideoFrame(0, TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() =>
            processor.Apply(frame, [new RedactionInstruction(new PixelRect(0, 0, 1, 1), RedactionEffectKind.Pixelate)], 0.5));
    }

    // stands in for an OpenCV effect: only records the roi it was given
    private sealed class RecordingEffect(RedactionEffectKind kind) : IRedactionEffect
    {
        public List<PixelRect> Calls { get; } = [];

        public RedactionEffectKind Kind => kind;

        public void Apply(OwnedVideoFrame frame, PixelRect roi, double strength) => Calls.Add(roi);
    }
}

public class RedactionRegionStoreTests
{
    [Fact]
    public void Add_update_remove_publish_new_snapshots_and_notify()
    {
        var store = new RedactionRegionStore();
        var changes = 0;
        store.Changed += (_, _) => changes++;
        var region = RedactionRegion.Create(new NormalizedRect(0, 0, 0.1, 0.1), 0, 10, RedactionEffectKind.Pixelate);

        store.Add(region);
        var before = store.Regions;
        Assert.True(store.Update(region with { EndFrame = 99 }));
        Assert.Equal(10, before[0].EndFrame);                    // earlier snapshot unchanged (readers are safe)
        Assert.Equal(99, store.Regions[0].EndFrame);
        Assert.False(store.Update(region with { Id = Guid.NewGuid() }));
        Assert.True(store.Remove(region.Id));
        Assert.False(store.Remove(region.Id));

        Assert.Empty(store.Regions);
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task Concurrent_writers_never_lose_updates()
    {
        var store = new RedactionRegionStore();

        await Parallel.ForAsync(0, 500, (i, _) =>
        {
            store.Add(RedactionRegion.Create(new NormalizedRect(0, 0, 0.1, 0.1), i, i, RedactionEffectKind.SolidBlack));
            return ValueTask.CompletedTask;
        });

        Assert.Equal(500, store.Regions.Count);
    }
}
