using VisionRedact.Domain.Media;

namespace VisionRedact.Application.Media;

// one open video file: read in order, seek. EmguVideoDecoder (OpenCV's FFmpeg backend) is the real one,
// IVideoDecoderFactory makes them, and every pipeline opens its own (playback, scan, export, previews...).
// sync on purpose: decoding is blocking native work, an async API would still block a thread.
// not thread-safe, one owner makes all the calls
// TODO: hardware decode (D3D11VA / NVDEC) would go behind this, ok for now (docs/Performance.md, known limits)
public interface IVideoDecoder : IDisposable
{
    VideoMetadata Metadata { get; }

    // e.g. "FFMPEG", diagnostics only
    string BackendName { get; }

    // the frame the next ReadNext returns
    long Position { get; }

    // clamped to the video
    void Seek(long frameIndex);

    /// <summary>Next frame, now owned by the caller (dispose it or hand it on). Null at the end.</summary>
    // remember: always a new frame, never a buffer the decoder reuses (see OwnedVideoFrame)
    OwnedVideoFrame? ReadNext();

    // one frame forward w/o making a picture, cheaper than ReadNext. false at the end
    bool Skip();
}
