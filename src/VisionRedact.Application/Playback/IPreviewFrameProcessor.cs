using VisionRedact.Application.Media;

namespace VisionRedact.Application.Playback;

// runs on the redaction task, between decode and the decode-ahead buffer. RedactionPreviewProcessor is the real one.
// may change the pixels in place, it's a decoded copy (the file is never touched).
// must be fast (per frame, the slower of decode and this sets the pace) and thread-safe for what it reads,
// b/c the UI edits regions and detections while it runs
public interface IPreviewFrameProcessor
{
    void Process(OwnedVideoFrame frame);
}
