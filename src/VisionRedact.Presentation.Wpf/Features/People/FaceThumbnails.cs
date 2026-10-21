using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionRedact.Application.Media;
using VisionRedact.Application.Workspace;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;

namespace VisionRedact.Presentation.Wpf.Features.People;

// face pictures for the People tab: a crop around a track's best box, cached by frame + box.
// read on demand through the open video's VideoFrameReader, not the playback decoder.
// don't forget: never written to disk, a face crop is as sensitive as the video.
// frozen, so every row showing the same face shares one bitmap
public sealed class FaceThumbnails(VideoWorkspace workspace, IFrameImaging imaging)
{
    private const int Size = 72;
    private readonly Dictionary<(long Frame, NormalizedRect Box), Task<ImageSource?>> _cache = []; // UI thread only
    private string? _videoPath;

    // null if the frame can't be read. UI thread
    public Task<ImageSource?> GetAsync(Detection face)
    {
        var path = workspace.CurrentSession?.Metadata.SourcePath;
        if (path != _videoPath)
        {
            _cache.Clear(); // another video: old pictures belong to it
            _videoPath = path;
        }

        // frame + box, not the track: a track's best face can change after a re-scan
        var key = (face.FrameIndex, face.Box);
        if (!_cache.TryGetValue(key, out var picture))
        {
            picture = LoadAsync(face);
            _cache[key] = picture;
        }

        return picture;
    }

    private async Task<ImageSource?> LoadAsync(Detection face)
    {
        if (workspace.FrameReader is not { } reader)
        {
            return null;
        }

        try
        {
            // a bit of margin, looks like a portrait, not a tight crop
            var image = await reader.ReadAsync(face.FrameIndex, frame => imaging.Resize(frame, face.Box.Inflate(0.25), Size, Size));
            return image is null ? null : ToFrozenBitmap(image);
        }
        catch (MediaException)
        {
            return null;
        }
    }

    private static BitmapSource ToFrozenBitmap(ImageBuffer image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr24, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
