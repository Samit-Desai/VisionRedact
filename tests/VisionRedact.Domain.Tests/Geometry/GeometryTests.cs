using VisionRedact.Domain.Geometry;

namespace VisionRedact.Domain.Tests.Geometry;

public class NormalizedRectTests
{
    [Fact]
    public void Out_of_bounds_and_negative_coordinates_are_clamped_into_the_frame()
    {
        var r = new NormalizedRect(-0.2, 0.9, 0.5, 0.5);

        Assert.Equal(0, r.X);
        Assert.Equal(0.9, r.Y, 10);
        Assert.Equal(0.3, r.Width, 10);
        Assert.Equal(0.1, r.Height, 10);
    }

    [Fact]
    public void Inverted_boxes_are_normalized()
    {
        var r = NormalizedRect.FromCorners(0.8, 0.6, 0.2, 0.1);

        Assert.Equal(0.2, r.X, 10);
        Assert.Equal(0.1, r.Y, 10);
        Assert.Equal(0.6, r.Width, 10);
        Assert.Equal(0.5, r.Height, 10);
    }

    [Theory]
    [InlineData(double.NaN, 0, 0.1, 0.1)]
    [InlineData(0, double.PositiveInfinity, 0.1, 0.1)]
    [InlineData(0.5, 0.5, 0, 0.2)]
    [InlineData(1.5, 1.5, 0.2, 0.2)]
    public void Degenerate_input_becomes_empty(double x, double y, double w, double h)
    {
        Assert.True(new NormalizedRect(x, y, w, h).IsEmpty);
    }

    [Fact]
    public void ToPixels_rounds_outward_so_redaction_never_misses_a_pixel()
    {
        var r = new NormalizedRect(0.1001, 0.2001, 0.3, 0.3);

        var p = r.ToPixels(1000, 1000);

        Assert.Equal(new PixelRect(100, 200, 301, 301), p); // [100.1, 400.1] -> [100, 401)
    }

    [Fact]
    public void ToPixels_clamps_to_frame_and_handles_empty()
    {
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), new NormalizedRect(0, 0, 1, 1).ToPixels(1920, 1080));
        Assert.True(NormalizedRect.Empty.ToPixels(1920, 1080).IsEmpty);
    }

    [Fact]
    public void FromPixels_and_ToPixels_round_trip_on_integer_boxes()
    {
        var n = NormalizedRect.FromPixels(640, 360, 200, 120, 1920, 1080);

        Assert.Equal(new PixelRect(640, 360, 200, 120), n.ToPixels(1920, 1080));
    }

    [Fact]
    public void Inflate_adds_margin_but_stays_inside_the_frame()
    {
        var r = new NormalizedRect(0.0, 0.4, 0.2, 0.2).Inflate(0.25);

        Assert.Equal(0, r.X);
        Assert.Equal(0.35, r.Y, 10);
        Assert.Equal(0.25, r.Width, 10); // left side clipped at the edge
        Assert.Equal(0.3, r.Height, 10);
    }

    [Fact]
    public void Offset_moves_without_shrinking_at_the_edges()
    {
        var r = new NormalizedRect(0.7, 0.7, 0.2, 0.2).Offset(0.5, 0.5);

        Assert.Equal(0.8, r.X, 10);
        Assert.Equal(0.8, r.Y, 10);
        Assert.Equal(0.2, r.Width, 10);
    }

    [Fact]
    public void Intersection_over_union()
    {
        var a = new NormalizedRect(0, 0, 0.5, 0.5);
        var b = new NormalizedRect(0.25, 0, 0.5, 0.5);

        Assert.Equal(1.0 / 3.0, a.IntersectionOverUnion(b), 10);
        Assert.Equal(1.0, a.IntersectionOverUnion(a), 10);
        Assert.Equal(0, a.IntersectionOverUnion(new NormalizedRect(0.6, 0.6, 0.1, 0.1)));
    }

    [Fact]
    public void Lerp_interpolates_position_and_size_and_clamps_t()
    {
        var from = new NormalizedRect(0.1, 0.2, 0.1, 0.1);
        var to = new NormalizedRect(0.3, 0.4, 0.2, 0.3);

        var quarter = NormalizedRect.Lerp(from, to, 0.25);
        Assert.Equal(0.15, quarter.X, 10);
        Assert.Equal(0.25, quarter.Y, 10);
        Assert.Equal(0.125, quarter.Width, 10);
        Assert.Equal(0.15, quarter.Height, 10);
        Assert.Equal(from, NormalizedRect.Lerp(from, to, -1));
        Assert.Equal(to, NormalizedRect.Lerp(from, to, 2));
        Assert.Equal(from, NormalizedRect.Lerp(from, to, double.NaN));
    }
}

public class PixelRectTests
{
    [Fact]
    public void ClampTo_cuts_partially_outside_rects()
    {
        Assert.Equal(new PixelRect(0, 0, 50, 30), new PixelRect(-10, -20, 60, 50).ClampTo(100, 100));
        Assert.Equal(new PixelRect(90, 90, 10, 10), new PixelRect(90, 90, 50, 50).ClampTo(100, 100));
    }

    [Fact]
    public void ClampTo_returns_empty_for_rects_fully_outside()
    {
        Assert.True(new PixelRect(200, 200, 10, 10).ClampTo(100, 100).IsEmpty);
        Assert.True(new PixelRect(-50, 0, 10, 10).ClampTo(100, 100).IsEmpty);
    }
}

public class LetterboxTransformTests
{
    [Fact]
    public void Wide_content_in_a_taller_container_gets_bars_top_and_bottom()
    {
        // 1920x1080 video in a 640x640 model input
        var t = LetterboxTransform.Fit(new SizeD(1920, 1080), new SizeD(640, 640));

        Assert.Equal(1.0 / 3.0, t.Scale, 10);
        Assert.Equal(0, t.OffsetX, 10);
        Assert.Equal(140, t.OffsetY, 10);
        Assert.Equal(new RectD(0, 140, 640, 360), t.ContentRect);
    }

    [Fact]
    public void Model_box_maps_back_to_normalized_source_coordinates()
    {
        var t = LetterboxTransform.Fit(new SizeD(1920, 1080), new SizeD(640, 640));

        // the top-left quarter of the content area (not the bars), in model input px
        var n = t.ContainerToNormalized(new RectD(0, 140, 320, 180));

        Assert.Equal(0, n.X, 10);
        Assert.Equal(0, n.Y, 10);
        Assert.Equal(0.5, n.Width, 10);
        Assert.Equal(0.5, n.Height, 10);
        Assert.Equal(new PixelRect(0, 0, 960, 540), n.ToPixels(1920, 1080));
    }

    [Fact]
    public void Boxes_in_the_letterbox_bars_are_clipped()
    {
        var t = LetterboxTransform.Fit(new SizeD(1920, 1080), new SizeD(640, 640));

        Assert.True(t.ContainerToNormalized(new RectD(100, 0, 50, 100)).IsEmpty); // entirely in the top bar
        var partly = t.ContainerToNormalized(new RectD(0, 100, 64, 100));          // straddles the bar
        Assert.Equal(0, partly.Y);
        Assert.Equal(60.0 / 360.0, partly.Height, 10);
    }

    [Theory]
    [InlineData(800, 600)]
    [InlineData(1200, 400)]
    [InlineData(333.3, 777.7)]
    public void Display_round_trip_is_exact_for_any_container_size(double w, double h)
    {
        var t = LetterboxTransform.Fit(new SizeD(1920, 1080), new SizeD(w, h));
        var box = new NormalizedRect(0.25, 0.4, 0.1, 0.2);

        var back = t.ContainerToNormalized(t.NormalizedToContainer(box));

        Assert.Equal(box.X, back.X, 9);
        Assert.Equal(box.Y, back.Y, 9);
        Assert.Equal(box.Width, back.Width, 9);
        Assert.Equal(box.Height, back.Height, 9);
    }

    [Fact]
    public void Empty_sizes_produce_an_empty_transform()
    {
        Assert.True(LetterboxTransform.Fit(new SizeD(0, 1080), new SizeD(640, 640)).IsEmpty);
        Assert.True(LetterboxTransform.Fit(new SizeD(1920, 1080), new SizeD(0, 0)).IsEmpty);
        Assert.True(default(LetterboxTransform).ContainerToNormalized(new RectD(0, 0, 1, 1)).IsEmpty);
    }
}
