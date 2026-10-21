namespace VisionRedact.Application.Media;

// opens decoders: every pipeline gets its own decoder over the same file, and Application never builds Emgu CV types.
// careful: Open is sync and can be slow (probing, codec init, network shares), so UI callers move it off the UI
// thread (VideoWorkspace.OpenAsync). fails w/ a MediaException that says what kind (MediaErrorKind)
public interface IVideoDecoderFactory
{
    IVideoDecoder Open(string path);
}
