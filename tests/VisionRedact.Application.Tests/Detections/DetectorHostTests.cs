using VisionRedact.Application.Detections;
using VisionRedact.Application.Tests.Fakes;

namespace VisionRedact.Application.Tests.Detections;

public class DetectorHostTests
{
    [Fact]
    public async Task Detector_is_created_once_and_reused()
    {
        var factory = new FakeObjectDetectorFactory(() => new FakeObjectDetector());
        using var host = new DetectorHost(factory);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => host.GetAsync(FakeObjectDetectorFactory.Model)));

        Assert.Equal(1, factory.Creates);
        Assert.All(results, d => Assert.Same(results[0], d));
    }

    [Fact]
    public async Task Load_failure_surfaces_and_a_later_call_retries()
    {
        var attempts = 0;
        var factory = new FakeObjectDetectorFactory(() => ++attempts == 1
            ? throw new DetectionException(DetectionErrorKind.ModelNotFound, "missing")
            : new FakeObjectDetector());
        using var host = new DetectorHost(factory);

        var error = await Assert.ThrowsAsync<DetectionException>(() => host.GetAsync(FakeObjectDetectorFactory.Model));
        Assert.Equal(DetectionErrorKind.ModelNotFound, error.Kind);

        Assert.NotNull(await host.GetAsync(FakeObjectDetectorFactory.Model));
    }

    [Fact]
    public async Task Several_models_run_on_every_frame_and_are_each_loaded_once()
    {
        var face = new FakeObjectDetector();
        var person = new FakeObjectDetector();
        var queue = new Queue<IObjectDetector>([face, person]);
        var factory = new FakeObjectDetectorFactory(() => queue.Dequeue());
        using var host = new DetectorHost(factory);
        IReadOnlyList<DetectionModel> models = [FakeObjectDetectorFactory.CreateModel("face"), FakeObjectDetectorFactory.CreateModel("person")];

        using var combined = await host.GetAsync(models);
        using var again = await host.GetAsync(models);
        using var frame = new TestVideoFrame(3, TimeSpan.Zero);
        var results = combined.Detect(frame);

        Assert.Equal(2, factory.Creates);
        Assert.Equal(2, results.Count);
        Assert.Equal(1, face.Calls);
        Assert.Equal(1, person.Calls);
        // careful: combined and again are still alive here (using var), so this does NOT check that disposing
        // the combined handle leaves the shared detectors loaded
        Assert.False(face.IsDisposed);
    }

    [Fact]
    public async Task Changing_the_device_loads_a_separate_session_and_keeps_the_old_one_cached()
    {
        var factory = new FakeObjectDetectorFactory(() => new FakeObjectDetector());
        using var host = new DetectorHost(factory);
        var gpu = new InferenceTarget(InferenceDevice.DirectML, 1, "GPU");

        var onCpu = await host.GetAsync(FakeObjectDetectorFactory.Model);
        host.Target = gpu;
        var onGpu = await host.GetAsync(FakeObjectDetectorFactory.Model);
        host.Target = InferenceTarget.Cpu;
        var cpuAgain = await host.GetAsync(FakeObjectDetectorFactory.Model);

        Assert.NotSame(onCpu, onGpu);
        Assert.Same(onCpu, cpuAgain);
        Assert.Equal([InferenceTarget.Cpu, gpu], factory.Targets);
    }

    [Fact]
    public async Task Dispose_releases_the_detector()
    {
        var detector = new FakeObjectDetector();
        var host = new DetectorHost(new FakeObjectDetectorFactory(() => detector));
        await host.GetAsync(FakeObjectDetectorFactory.Model);

        host.Dispose();

        Assert.True(detector.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.GetAsync(FakeObjectDetectorFactory.Model));
    }
}

public class FrameDetectionServiceTests
{
    [Fact]
    public async Task Detects_the_requested_frame_and_stores_the_result()
    {
        var decoder = new FakeVideoDecoder(frameCount: 500);
        var detector = new FakeObjectDetector();
        var repository = new InMemoryDetectionRepository();
        repository.Reset(500);
        using var host = new DetectorHost(new FakeObjectDetectorFactory(() => detector));
        var service = new FrameDetectionService(new FakeVideoDecoderFactory(_ => decoder), host, repository);

        var detections = await service.DetectFrameAsync("a.mp4", 321, [FakeObjectDetectorFactory.Model]);

        var d = Assert.Single(detections);
        Assert.Equal(321, d.FrameIndex);
        Assert.Equal([321L], detector.SeenFrames);
        Assert.Single(repository.GetForFrame(321));
        Assert.True(decoder.IsDisposed);                                   // own decoder, released
        Assert.All(decoder.CreatedFrames, f => Assert.True(f.IsDisposed)); // frame released after inference
    }
}
