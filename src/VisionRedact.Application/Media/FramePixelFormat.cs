namespace VisionRedact.Application.Media;

// how the bytes of an OwnedVideoFrame are laid out. decoders hand out only these. one format so far,
// so the scan, the blur and the presenter all use the same frames as they are
public enum FramePixelFormat
{
    // 8 bit B, G, R: OpenCV's own order, same as WPF PixelFormats.Bgr24
    Bgr24 = 0,
}
