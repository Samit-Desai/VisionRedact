using VisionRedact.Domain.Geometry;

namespace VisionRedact.Application.Media;

// small pictures from frames: timeline tiles and hover previews, and the People tab's face thumbnails.
// EmguFrameImaging (Infrastructure.Video) does it, b/c Application can't reference OpenCV
public interface IFrameImaging
{
    // copies region (whole frame if null) into a new picture that fits maxWidth x maxHeight, aspect kept.
    // doesn't take ownership of the frame
    ImageBuffer Resize(OwnedVideoFrame frame, NormalizedRect? region, int maxWidth, int maxHeight);
}
