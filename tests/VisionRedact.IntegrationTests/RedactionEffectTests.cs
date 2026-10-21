using Emgu.CV;
using Emgu.CV.CvEnum;
using VisionRedact.Application.Redaction;
using VisionRedact.Domain.Geometry;
using VisionRedact.Infrastructure.Video;

namespace VisionRedact.IntegrationTests;

// real OpenCV effects on real frame memory: the roi changes, nothing outside it does
public sealed class RedactionEffectTests
{
    private static readonly PixelRect Roi = new(40, 30, 64, 48);

    [Theory]
    [InlineData(typeof(GaussianBlurEffect))]
    [InlineData(typeof(PixelateEffect))]
    [InlineData(typeof(SolidBlackEffect))]
    public void Effect_modifies_only_the_region(Type effectType)
    {
        var effect = (IRedactionEffect)Activator.CreateInstance(effectType)!;
        using var frame = Checkerboard();
        var before = frame.GetPixels().ToArray();

        effect.Apply(frame, Roi, strength: 0.8);

        var after = frame.GetPixels();
        var changedInside = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var i = (y * frame.Stride) + (x * 3);
                var inside = x >= Roi.X && x < Roi.Right && y >= Roi.Y && y < Roi.Bottom;
                var changed = before[i] != after[i] || before[i + 1] != after[i + 1] || before[i + 2] != after[i + 2];
                if (inside && changed)
                {
                    changedInside++;
                }
                else if (!inside)
                {
                    Assert.False(changed, $"{effectType.Name} changed pixel ({x},{y}) outside the region.");
                }
            }
        }

        Assert.True(changedInside > Roi.Area / 3, $"{effectType.Name} changed only {changedInside} of {Roi.Area} region pixels.");
    }

    [Fact]
    public void Solid_black_leaves_no_information()
    {
        using var frame = Checkerboard();

        new SolidBlackEffect().Apply(frame, Roi, 0.5);

        var pixels = frame.GetPixels();
        for (var y = Roi.Y; y < Roi.Bottom; y++)
        {
            for (var x = Roi.X * 3; x < Roi.Right * 3; x++)
            {
                Assert.Equal(0, pixels[(y * frame.Stride) + x]);
            }
        }
    }

    [Fact]
    public void Blur_destroys_fine_detail()
    {
        using var frame = Checkerboard();

        new GaussianBlurEffect().Apply(frame, Roi, 1.0);

        // a 2 px checkerboard is all fine detail: after the blur the center of the region is flat gray
        var pixels = frame.GetPixels();
        var centre = ((Roi.Y + (Roi.Height / 2)) * frame.Stride) + ((Roi.X + (Roi.Width / 2)) * 3);
        Assert.InRange(pixels[centre], 100, 155);
        Assert.InRange(pixels[centre + 3], 100, 155);
    }

    private static MatVideoFrame Checkerboard()
    {
        var mat = new Mat(120, 160, DepthType.Cv8U, 3);
        var data = new byte[120 * 160 * 3];
        for (var y = 0; y < 120; y++)
        {
            for (var x = 0; x < 160; x++)
            {
                var v = (byte)((((x / 2) + (y / 2)) % 2 == 0) ? 255 : 0);
                var i = ((y * 160) + x) * 3;
                data[i] = v;
                data[i + 1] = v;
                data[i + 2] = v;
            }
        }

        mat.SetTo(data);
        return new MatVideoFrame(mat, 0, TimeSpan.Zero);
    }
}
