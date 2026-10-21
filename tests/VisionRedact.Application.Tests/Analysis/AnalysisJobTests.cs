using VisionRedact.Application.Analysis;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Tests.Fakes;
using VisionRedact.Application.Tracking;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Jobs;

namespace VisionRedact.Application.Tests.Analysis;

// the scan pipeline w/ a fake decoder + detector: order, sampling, backpressure, cancel, failures
public class AnalysisJobTests
{
    private readonly InMemoryDetectionRepository _repository = new();
    private readonly ProcessingJobManager _manager = new();

    [Fact]
    public async Task Every_sampled_frame_is_analyzed_once_in_order_and_stored()
    {
        var decoder = new FakeVideoDecoder(frameCount: 200);
        var detector = new FakeObjectDetector();
        var job = CreateJob(decoder, detector, new EveryFrameSampling());

        await Run(job);

        Assert.Equal(ProcessingJobState.Completed, job.State);
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (long)i), detector.SeenFrames);
        Assert.Equal(200, _repository.Count);
        Assert.Equal(200, _repository.AnalyzedFrameCount);
        Assert.Equal(200, job.GetStatistics().FramesAnalyzed);
    }

    [Fact]
    public async Task Sampling_skips_frames_without_decoding_their_pixels()
    {
        var decoder = new FakeVideoDecoder(frameCount: 100);
        var detector = new FakeObjectDetector();
        var job = CreateJob(decoder, detector, new EveryNthFrameSampling(5));

        await Run(job);

        Assert.Equal([0L, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95], detector.SeenFrames);
        Assert.Equal(20, decoder.CreatedFrames.Count); // skipped frames never got a pixel buffer (Performance.md, optimization 1)
        Assert.Equal(4, _repository.HoldFrames);
        Assert.Single(_repository.GetForDisplay(98)); // frame 95's box is held up to 99
    }

    [Fact]
    public async Task Producer_faster_than_consumer_is_held_back_by_the_bounded_channel()
    {
        // decode is instant, the detector is slow. w/o the bound the decoder would race to the end of the video
        var decoder = new FakeVideoDecoder(frameCount: 10_000);
        var detector = new FakeObjectDetector { Latency = TimeSpan.FromMilliseconds(15) };
        var job = CreateJob(decoder, detector, new EveryFrameSampling(), capacity: 4);

        _manager.Start(job);
        var maxLive = 0;
        var maxAhead = 0L;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(10);
            maxLive = Math.Max(maxLive, decoder.CreatedFrames.Count(f => !f.IsDisposed));
            maxAhead = Math.Max(maxAhead, decoder.Position - detector.Calls);
            Assert.True(job.GetStatistics().ChannelCount <= 4);
        }

        job.Cancel();
        await job.Completion;

        // 4 in the channel + 1 in the detector + 1 decoded and waiting in WriteAsync
        Assert.InRange(maxLive, 1, 6);
        Assert.InRange(maxAhead, 0, 6);
    }

    [Fact]
    public async Task Cancellation_stops_both_sides_and_releases_every_frame()
    {
        var decoder = new FakeVideoDecoder(frameCount: 100_000);
        var detector = new FakeObjectDetector { Latency = TimeSpan.FromMilliseconds(5) };
        var job = CreateJob(decoder, detector, new EveryFrameSampling());

        _manager.Start(job);
        await WaitFor(() => detector.Calls >= 5);
        job.Cancel();
        await job.Completion;

        Assert.Equal(ProcessingJobState.Cancelled, job.State);
        Assert.True(decoder.IsDisposed);
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed, $"{f} leaked"));
        Assert.True(detector.Calls < 1000);
    }

    [Fact]
    public async Task Inference_failure_fails_the_job_with_the_root_cause_and_releases_frames()
    {
        var decoder = new FakeVideoDecoder(frameCount: 1_000);
        var detector = new FakeObjectDetector { FailAtFrame = 42 };
        var job = CreateJob(decoder, detector, new EveryFrameSampling());

        await Run(job);

        Assert.Equal(ProcessingJobState.Failed, job.State);
        var error = Assert.IsType<DetectionException>(job.Error);
        Assert.Equal(DetectionErrorKind.InferenceFailed, error.Kind);
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed, $"{f} leaked"));
        Assert.True(decoder.Position < 100, "Producer must stop soon after the consumer failed.");
        Assert.Equal(42, _repository.AnalyzedFrameCount); // results before the failure are kept
    }

    [Fact]
    public async Task Decoder_failure_fails_the_job()
    {
        var decoder = new FakeVideoDecoder(frameCount: 1_000) { FailAtFrame = 10 };
        var job = CreateJob(decoder, new FakeObjectDetector(), new EveryFrameSampling());

        await Run(job);

        Assert.Equal(ProcessingJobState.Failed, job.State);
        Assert.IsType<InvalidOperationException>(job.Error);
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed));
    }

    [Fact]
    public async Task Stream_shorter_than_metadata_completes_normally()
    {
        var decoder = new FakeVideoDecoder(frameCount: 500, decodableFrames: 123);
        var job = CreateJob(decoder, new FakeObjectDetector(), new EveryFrameSampling());

        await Run(job);

        Assert.Equal(ProcessingJobState.Completed, job.State);
        Assert.Equal(123, job.GetStatistics().FramesAnalyzed);
    }

    [Fact]
    public async Task Job_releases_its_detector_handle_when_done()
    {
        var detector = new FakeObjectDetector();
        var job = CreateJob(new FakeVideoDecoder(frameCount: 10), detector, new EveryFrameSampling());

        await Run(job);

        Assert.True(detector.IsDisposed);
    }

    [Fact]
    public async Task A_second_run_numbers_its_tracks_after_the_first_so_reviewed_tracks_never_collide()
    {
        // regression (ADR-0024): ids used to restart at 1 each run. run 1's approved face survives the re-scan,
        // so run 2 could reuse its id, and the approved box could slide toward a different face (= a leak)
        var decoder = new FakeVideoDecoder(frameCount: 10);
        _repository.Reset(decoder.Metadata.FrameCount);
        await Run(CreateTrackedJob(decoder));
        var firstRunTrack = _repository.Snapshot()[0].TrackId!.Value;
        _repository.UpdateStatusWhere(_ => true, ReviewStatus.Approved);

        await Run(CreateTrackedJob(new FakeVideoDecoder(frameCount: 10)));

        var newTracks = _repository.Snapshot()
            .Where(d => d.Status == ReviewStatus.Pending)
            .Select(d => d.TrackId!.Value)
            .Distinct();
        Assert.All(newTracks, id => Assert.True(id > firstRunTrack, $"Track {id} reuses a number from run 1."));
    }

    private AnalysisJob CreateTrackedJob(FakeVideoDecoder decoder) =>
        new("video.mp4", new FakeVideoDecoderFactory(_ => decoder), new FakeObjectDetector(), _repository, new EveryFrameSampling(), new IouObjectTracker());

    private AnalysisJob CreateJob(FakeVideoDecoder decoder, FakeObjectDetector detector, IFrameSamplingStrategy sampling, int capacity = 4)
    {
        _repository.Reset(decoder.Metadata.FrameCount);
        return new AnalysisJob("video.mp4", new FakeVideoDecoderFactory(_ => decoder), detector, _repository, sampling, options: new AnalysisOptions { ChannelCapacity = capacity });
    }

    private async Task Run(AnalysisJob job)
    {
        _manager.Start(job);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(5);
        }
    }
}
