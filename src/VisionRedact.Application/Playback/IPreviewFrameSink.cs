using VisionRedact.Application.Media;

namespace VisionRedact.Application.Playback;

// end of the playback pipe. WriteableBitmapFramePresenter (WPF side) is the real one, so this layer never sees WPF.
// the only way frames reach the UI: no EventAggregator, no view model property per frame.
// PlaybackSession calls Present, VideoWorkspace calls Clear when the video closes. safe from any thread
public interface IPreviewFrameSink
{
    // called on the present task, must not block (no Dispatcher.Invoke). takes ownership of the frame.
    // a frame not shown yet may be disposed when a newer one comes, a stale preview frame is worth nothing
    void Present(OwnedVideoFrame frame);

    // disposes a frame received but not shown yet
    void Clear();
}
