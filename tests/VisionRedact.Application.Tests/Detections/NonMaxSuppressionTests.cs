using VisionRedact.Application.Detections;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Application.Tests.Detections;

public class NonMaxSuppressionTests
{
    private static DetectionCandidate Face(double x, double y, double size, float confidence) =>
        new(new NormalizedRect(x, y, size, size), confidence, ObjectClass.Face);

    [Fact]
    public void Overlapping_boxes_collapse_to_the_highest_score()
    {
        var result = NonMaxSuppression.Apply(
            [Face(0.10, 0.10, 0.2, 0.80f), Face(0.11, 0.10, 0.2, 0.95f), Face(0.12, 0.11, 0.2, 0.90f)],
            iouThreshold: 0.3f);

        var kept = Assert.Single(result);
        Assert.Equal(0.95f, kept.Confidence);
    }

    [Fact]
    public void Separate_objects_are_all_kept_in_score_order()
    {
        var result = NonMaxSuppression.Apply(
            [Face(0.1, 0.1, 0.1, 0.8f), Face(0.6, 0.6, 0.1, 0.9f), Face(0.1, 0.7, 0.1, 0.7f)],
            iouThreshold: 0.3f);

        Assert.Equal([0.9f, 0.8f, 0.7f], result.Select(r => r.Confidence));
    }

    [Fact]
    public void Different_classes_never_suppress_each_other()
    {
        var face = Face(0.1, 0.1, 0.3, 0.9f);
        var person = face with { Class = ObjectClass.Person, Confidence = 0.8f };

        Assert.Equal(2, NonMaxSuppression.Apply([face, person], 0.3f).Count);
    }

    [Fact]
    public void Max_results_caps_output()
    {
        var many = Enumerable.Range(0, 50).Select(i => Face(i * 0.019, 0.5, 0.01, 0.5f + (i * 0.001f))).ToArray();

        Assert.Equal(10, NonMaxSuppression.Apply(many, 0.3f, maxResults: 10).Count);
    }

    [Fact]
    public void Empty_and_single_inputs_are_returned_as_is()
    {
        Assert.Empty(NonMaxSuppression.Apply([], 0.3f));
        Assert.Single(NonMaxSuppression.Apply([Face(0, 0, 0.1, 0.5f)], 0.3f));
    }

    [Fact]
    public void Threshold_is_exclusive_on_iou()
    {
        // IoU of these two is exactly 1/3.
        var a = new DetectionCandidate(new NormalizedRect(0, 0, 0.5, 0.5), 0.9f, ObjectClass.Face);
        var b = new DetectionCandidate(new NormalizedRect(0.25, 0, 0.5, 0.5), 0.8f, ObjectClass.Face);

        Assert.Equal(2, NonMaxSuppression.Apply([a, b], iouThreshold: 0.34f).Count);
        Assert.Single(NonMaxSuppression.Apply([a, b], iouThreshold: 0.33f));
    }
}
