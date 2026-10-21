using VisionRedact.Application.Export;
using VisionRedact.Application.Media;
using VisionRedact.Domain.Media;

namespace VisionRedact.Application.Tests.Fakes;

// stands in for the encoder: keeps (frame, first byte) instead of encoding. creates the file so the final rename works
internal sealed class FakeVideoWriterFactory : IVideoWriterFactory
{
    public List<(long Frame, byte Marker)> Written { get; } = [];

    public long? FailAtFrame { get; init; }

    public bool DropFrames { get; init; }

    public string? CreatedPath { get; private set; }

    public IVideoWriter Create(string path, VideoMetadata source, ExportFormat format)
    {
        CreatedPath = path;
        File.WriteAllText(path, "partial");
        return new Writer(this);
    }

    private sealed class Writer(FakeVideoWriterFactory owner) : IVideoWriter
    {
        public string Codec => "fake";

        public ExportAudio Audio => ExportAudio.NotSupported;

        public long FramesWritten { get; private set; }

        public void Write(OwnedVideoFrame frame)
        {
            if (owner.FailAtFrame == frame.FrameIndex)
            {
                throw new IOException("Simulated disk full.");
            }

            owner.Written.Add((frame.FrameIndex, frame.GetPixels()[0]));
            if (!owner.DropFrames)
            {
                FramesWritten++;
            }
        }

        public void Complete() => owner.Completed = true;

        public void Dispose()
        {
        }
    }

    public bool Completed { get; private set; }
}

// stands in for VideoExportService in the ExportJob test: only reports progress and honors cancel
internal sealed class FakeExportService(int frames, TimeSpan perFrame) : IVideoExportService
{
    public async Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        for (var i = 1; i <= frames; i++)
        {
            await Task.Delay(perFrame, cancellationToken);
            progress?.Report(new ExportProgress(i, frames, 0));
        }

        return new ExportResult(request.OutputPath, "fake", frames, 0, TimeSpan.Zero);
    }
}
