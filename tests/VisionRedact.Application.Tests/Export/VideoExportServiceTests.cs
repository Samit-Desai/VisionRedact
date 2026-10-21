using VisionRedact.Application.Detections;
using VisionRedact.Application.Export;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.Redaction;
using VisionRedact.Application.Tests.Fakes;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.Jobs;
using VisionRedact.Domain.Redaction;

namespace VisionRedact.Application.Tests.Export;

public sealed class VideoExportServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vr-export-").FullName;
    private readonly FakeVideoWriterFactory _writer = new();
    private readonly MarkingProcessor _processor = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Every_frame_is_written_in_order_and_the_output_appears_only_when_complete()
    {
        var decoder = new FakeVideoDecoder(frameCount: 120);
        var output = Output("out.mp4");

        var result = await Service(decoder).ExportAsync(Request(output));

        Assert.Equal(120, result.FramesWritten);
        Assert.Equal(Enumerable.Range(0, 120).Select(i => (long)i), _writer.Written.Select(w => w.Frame));
        Assert.True(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial-*"));
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed));
    }

    [Fact]
    public async Task Redactions_from_the_snapshot_are_applied_to_exactly_the_right_frames()
    {
        var decoder = new FakeVideoDecoder(frameCount: 50);
        var repository = new InMemoryDetectionRepository();
        repository.Reset(50);
        repository.AddFrameResults(10, [Face(10)]);
        repository.AddFrameResults(20, [Face(20) with { Status = ReviewStatus.Rejected }]);
        var regions = new RedactionRegionStore();
        regions.Add(RedactionRegion.Create(new NormalizedRect(0, 0, 0.5, 0.5), 30, 32, RedactionEffectKind.SolidBlack));
        var snapshot = RedactionSnapshot.Capture(50, repository, regions);

        var result = await Service(decoder).ExportAsync(new ExportRequest("source.mp4", Output("o.mp4"), ExportFormat.Mp4, snapshot));

        Assert.Equal(4, result.FramesRedacted); // frame 10 + frames 30..32; rejected frame 20 is not redacted
        Assert.Equal([10L, 30, 31, 32], _writer.Written.Where(w => w.Marker == MarkingProcessor.Marker).Select(w => w.Frame));
    }

    [Fact]
    public void Snapshot_is_isolated_from_later_review_changes()
    {
        var repository = new InMemoryDetectionRepository();
        repository.Reset(10);
        var face = Face(5);
        repository.AddFrameResults(5, [face]);
        var snapshot = RedactionSnapshot.Capture(10, repository, new RedactionRegionStore());

        repository.UpdateStatus(face.Id, ReviewStatus.Rejected); // reviewer keeps working during export

        Assert.Single(snapshot.PlanFor(5, 100, 100));
    }

    [Fact]
    public async Task Cancellation_leaves_no_partial_file_and_releases_frames()
    {
        var decoder = new FakeVideoDecoder(frameCount: 100_000, decodeLatency: TimeSpan.FromMilliseconds(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var output = Output("cancelled.mp4");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(decoder).ExportAsync(Request(output), cancellationToken: cts.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed));
    }

    [Fact]
    public async Task Writer_failure_leaves_no_partial_file()
    {
        var writer = new FakeVideoWriterFactory { FailAtFrame = 7 };
        var decoder = new FakeVideoDecoder(frameCount: 20);

        await Assert.ThrowsAsync<IOException>(() => new VideoExportService(new FakeVideoDecoderFactory(_ => decoder), writer, _processor).ExportAsync(Request(Output("x.mp4"))));

        Assert.Empty(Directory.GetFiles(_directory));
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed));
    }

    [Fact]
    public async Task Redaction_stage_failure_reports_the_root_cause_and_releases_every_frame()
    {
        // the middle stage throws while decode waits on a full channel and encode waits for input.
        // both must stop, the real error must come out (not the cancel it causes), and every buffered frame is disposed
        var decoder = new FakeVideoDecoder(frameCount: 500);
        var repository = new InMemoryDetectionRepository();
        repository.Reset(500);
        repository.AddFrameResults(40, [Face(40)]);
        var snapshot = RedactionSnapshot.Capture(500, repository, new RedactionRegionStore());
        var service = new VideoExportService(new FakeVideoDecoderFactory(_ => decoder), _writer, new ThrowingProcessor());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(new ExportRequest("source.mp4", Output("x.mp4"), ExportFormat.Mp4, snapshot)));

        Assert.Equal("Simulated effect failure.", error.Message);
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed));
        Assert.True(_writer.Written.Count <= 40, "frames after the failing one must not be encoded");
    }

    [Fact]
    public async Task Encoder_that_drops_frames_is_detected_as_incomplete_output()
    {
        var writer = new FakeVideoWriterFactory { DropFrames = true };

        var error = await Assert.ThrowsAsync<ExportException>(() =>
            new VideoExportService(new FakeVideoDecoderFactory(_ => new FakeVideoDecoder(frameCount: 5)), writer, _processor).ExportAsync(Request(Output("x.mp4"))));

        Assert.Equal(ExportErrorKind.IncompleteOutput, error.Kind);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Source_file_can_never_be_the_output()
    {
        var source = Output("same.mp4");

        var error = await Assert.ThrowsAsync<ExportException>(() =>
            Service(new FakeVideoDecoder()).ExportAsync(new ExportRequest(source, source.ToUpperInvariant(), ExportFormat.Mp4, RedactionSnapshot.Empty(100))));

        Assert.Equal(ExportErrorKind.SameAsSource, error.Kind);
    }

    [Fact]
    public async Task Missing_output_folder_is_rejected_up_front()
    {
        var error = await Assert.ThrowsAsync<ExportException>(() =>
            Service(new FakeVideoDecoder()).ExportAsync(Request(Path.Combine(_directory, "missing", "o.mp4"))));

        Assert.Equal(ExportErrorKind.InvalidOutputPath, error.Kind);
    }

    [Fact]
    public async Task Export_job_reports_progress_and_can_be_cancelled()
    {
        var manager = new ProcessingJobManager();
        var slow = new ExportJob(new FakeExportService(10_000, TimeSpan.FromMilliseconds(1)), Request(Output("a.mp4")));
        var fast = new ExportJob(new FakeExportService(20, TimeSpan.Zero), Request(Output("b.mp4")));

        manager.Start(fast);
        manager.Start(slow);
        await fast.Completion;
        await Task.Delay(50);
        slow.Cancel();
        await slow.Completion;

        Assert.Equal(ProcessingJobState.Completed, fast.State);
        Assert.Equal(20, fast.Result!.FramesWritten);
        Assert.Equal(ProcessingJobState.Cancelled, slow.State);
    }

    [Fact]
    public async Task Source_that_stops_early_is_exported_and_reported()
    {
        // a damaged packet or a cut-off file: OpenCV just stops, as if the video had ended
        var decoder = new FakeVideoDecoder(frameCount: 100, decodableFrames: 60);

        var result = await Service(decoder).ExportAsync(Request(Output("out.mp4")));

        Assert.Equal(60, result.FramesWritten);
        Assert.Equal(100, result.FramesExpected);
        Assert.True(result.SourceEndedEarly);
    }

    [Fact]
    public async Task Small_container_frame_count_mismatch_is_not_reported()
    {
        // some containers only estimate the frame count (duration * fps), so a few frames short is normal
        var decoder = new FakeVideoDecoder(frameCount: 100, decodableFrames: 98);

        var result = await Service(decoder).ExportAsync(Request(Output("out.mp4")));

        Assert.Equal(98, result.FramesWritten);
        Assert.False(result.SourceEndedEarly);
    }

    [Fact]
    public void Snapshot_coverage_is_frozen_when_export_starts()
    {
        var live = new InMemoryDetectionRepository();
        live.Reset(10);
        live.AddFrameResults(0, []);

        var snapshot = RedactionSnapshot.Capture(10, live, new RedactionRegionStore());
        for (long frame = 1; frame < 10; frame++)
        {
            live.AddFrameResults(frame, []); // the scan finishes after Export was pressed
        }

        Assert.Equal(1, snapshot.Coverage.CoveredFrames);
        Assert.False(snapshot.Coverage.IsComplete);
    }

    private static Detection Face(long frame) => Detection.Create(frame, new NormalizedRect(0.1, 0.1, 0.3, 0.3), 0.9f, ObjectClass.Face);

    private string Output(string name) => Path.Combine(_directory, name);

    private ExportRequest Request(string output) => new("source.mp4", output, ExportFormat.Mp4, RedactionSnapshot.Empty(100_000));

    private VideoExportService Service(FakeVideoDecoder decoder) => new(new FakeVideoDecoderFactory(_ => decoder), _writer, _processor);

    // stands in for RedactionProcessor: stamps a marker in the first pixel, so a test sees which frames got redacted
    private sealed class MarkingProcessor : IRedactionProcessor
    {
        public const byte Marker = 0xEE;

        public unsafe void Apply(OwnedVideoFrame frame, IReadOnlyList<RedactionInstruction> instructions, double strength) =>
            ((byte*)frame.DataPointer)[0] = Marker;
    }

    private sealed class ThrowingProcessor : IRedactionProcessor
    {
        public void Apply(OwnedVideoFrame frame, IReadOnlyList<RedactionInstruction> instructions, double strength) =>
            throw new InvalidOperationException("Simulated effect failure.");
    }
}
