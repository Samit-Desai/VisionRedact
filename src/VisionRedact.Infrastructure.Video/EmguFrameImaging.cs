using System.Drawing;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Infrastructure.Video;

// crop + downscale into a managed byte[]: the hover preview, the storyboard tiles and the People thumbnails.
// no copies on the way: frame view -> crop view -> Resize writes straight into the pinned array.
// Area averaging keeps small thumbnails smooth, not aliased. no state, so thread-safe
public sealed class EmguFrameImaging : IFrameImaging
{
    public ImageBuffer Resize(OwnedVideoFrame frame, NormalizedRect? region, int maxWidth, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);

        // whole frame or the region, at least 1x1
        var source = region is { } r ? r.ToPixels(frame.Width, frame.Height) : new PixelRect(0, 0, frame.Width, frame.Height);
        source = source with { Width = Math.Max(1, source.Width), Height = Math.Max(1, source.Height) };

        // fit the box, keep the aspect, never upscale
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var stride = width * 3;
        var pixels = new byte[stride * height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var frameView = FrameViews.Wrap(frame);
            using var sourceView = new Mat(frameView, new Rectangle(source.X, source.Y, source.Width, source.Height));
            using var target = new Mat(height, width, DepthType.Cv8U, 3, handle.AddrOfPinnedObject(), stride);
            CvInvoke.Resize(sourceView, target, new Size(width, height), 0, 0, Inter.Area);
        }
        finally
        {
            handle.Free();
        }

        return new ImageBuffer(pixels, width, height, stride);
    }
}
