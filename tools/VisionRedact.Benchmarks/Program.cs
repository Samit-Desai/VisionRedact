using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using VisionRedact.Application.Analysis;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Export;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.Playback;
using VisionRedact.Application.Preview;
using VisionRedact.Application.Redaction;
using VisionRedact.Application.Tracking;
using VisionRedact.Domain.Detections;
using VisionRedact.Domain.Geometry;
using VisionRedact.Domain.Jobs;
using VisionRedact.Infrastructure.AI;
using VisionRedact.Infrastructure.Video;

// Measurement harness for docs/Performance.md. Every number printed here is measured, on this machine, now.
// Usage: dotnet run -c Release --project tools/VisionRedact.Benchmarks [real-video-path]
var realVideo = args.FirstOrDefault(File.Exists);
// Same OpenCV thread pool as the app, so the numbers match what the app does.
var openCvThreads = OpenCvThreads.Configure();
var work = Directory.CreateTempSubdirectory("vr-bench-").FullName;
var decoders = new EmguVideoDecoderFactory();
var options = new DetectorOptions { ModelsDirectory = Path.Combine(AppContext.BaseDirectory, "models") };
var models = new OnnxModelCatalog(options).GetModels().Where(m => m.IsAvailable).ToArray();
var targets = new DxgiAdapterCatalog().GetTargets();

if (args.Contains("--compare"))
{
    // B10: every argument after --compare that is not a flag is a video path (relative paths: current directory,
    // then the repository root). Options: --frames=N (frames used per video in every stage, default 1,800) and
    // --repeat=N (measured passes, default 3).
    var comparePaths = args.SkipWhile(a => a != "--compare").Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
    await RunCompareAsync(comparePaths, frameLimit: IntOption("--frames=", 1800), repeat: IntOption("--repeat=", 3));
    Directory.Delete(work, recursive: true);
    return;
}

if (args.Contains("--scenes"))
{
    if (realVideo is null)
    {
        Console.WriteLine("B9 needs a video path.");
        return;
    }

    Directory.Delete(work, recursive: true);
    RunSceneFinder(realVideo, sampleEvery: 6, windowSeconds: 10);
    return;
}

if (args.Contains("--tracking"))
{
    if (realVideo is null)
    {
        Console.WriteLine("B8 needs a real video path.");
        return;
    }

    RunTrackingAccuracy(realVideo, frameLimit: 3600, sampleEvery: 12);
    return;
}

Console.WriteLine($"# VisionRedact benchmarks — {DateTime.Now:yyyy-MM-dd HH:mm}");
Console.WriteLine($"CPU cores: {Environment.ProcessorCount} logical · OpenCV threads {openCvThreads} · .NET {Environment.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")} · Release={IsRelease()}");
Console.WriteLine();

var synthetic = Path.Combine(work, "synthetic-1080p.avi");
WriteSyntheticVideo(synthetic, 1920, 1080, 30, 900);
Console.WriteLine($"Synthetic source: 1920x1080 MJPG, 900 frames ({new FileInfo(synthetic).Length / 1_048_576} MB)");
if (realVideo is not null)
{
    Console.WriteLine($"Real source: {realVideo}");
}

if (args.Contains("--export"))
{
    // Export throughput only (the B6 export rows), for quick before/after comparisons.
    var exportRegions = new RedactionRegionStore();
    exportRegions.Add(RedactionRegion(0, 899));
    var exporter = new VideoExportService(decoders, new EmguVideoWriterFactory(), new RedactionProcessor([new GaussianBlurEffect(), new PixelateEffect(), new SolidBlackEffect()]));
    foreach (var format in new[] { ExportFormat.Mp4, ExportFormat.AviMjpeg })
    {
        var result = await exporter.ExportAsync(new ExportRequest(synthetic, Path.Combine(work, "export" + (format == ExportFormat.Mp4 ? ".mp4" : ".avi")), format, RedactionSnapshot.Capture(900, new InMemoryDetectionRepository(), exportRegions)));
        Console.WriteLine($"| export {format,-8} | {result.Codec} | {result.FramesWritten} frames in {result.Elapsed.TotalSeconds:0.0} s = {result.FramesWritten / result.Elapsed.TotalSeconds:0.0} frames/s (1080p, blur on every frame) |");
    }

    if (realVideo is not null)
    {
        // Real camera footage encodes harder than the synthetic clip: export its first 1,800 frames.
        const long realFrames = 1800;
        var realRegions = new RedactionRegionStore();
        realRegions.Add(RedactionRegion(0, realFrames - 1));
        var ffmpegWriters = new FfmpegVideoWriterFactory(new EmguVideoWriterFactory());
        IVideoWriterFactory[] writerFactories = ffmpegWriters.SelectedEncoder is null ? [new EmguVideoWriterFactory()] : [new EmguVideoWriterFactory(), ffmpegWriters];
        foreach (var writerFactory in writerFactories)
        {
            var realExporter = new VideoExportService(new LimitedDecoderFactory(decoders, realFrames), writerFactory, new RedactionProcessor([new GaussianBlurEffect(), new PixelateEffect(), new SolidBlackEffect()]));
            var output = Path.Combine(work, "real.mp4");
            var result = await realExporter.ExportAsync(new ExportRequest(realVideo, output, ExportFormat.Mp4, RedactionSnapshot.Capture(realFrames, new InMemoryDetectionRepository(), realRegions)));
            var writerName = writerFactory is FfmpegVideoWriterFactory ? "FFmpeg" : "OpenCV";
            Console.WriteLine($"| export Mp4 real {Path.GetExtension(realVideo).TrimStart('.').ToUpperInvariant()} via {writerName} | {result.Codec} | {result.FramesWritten} frames in {result.Elapsed.TotalSeconds:0.0} s = {result.FramesWritten / result.Elapsed.TotalSeconds:0.0} frames/s | {new FileInfo(output).Length / 1_048_576.0:0} MB (1080p, blur on every frame) |");
            File.Delete(output);
        }
    }

    Directory.Delete(work, recursive: true);
    return;
}

Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// B1. Analysis sampling: ReadNext()+Dispose on skipped frames (baseline) vs Skip() (current).
// ---------------------------------------------------------------------------------------------
Console.WriteLine("## B1 — Skipping unsampled frames: ReadNext()+Dispose vs Skip()  (1 of every 12 frames analyzed)");
foreach (var (label, path, frames) in Sources())
{
    var baseline = Scan(path, frames, sampleEvery: 12, useSkip: false);
    var current = Scan(path, frames, sampleEvery: 12, useSkip: true);
    Console.WriteLine($"| {label} | ReadNext+Dispose | {baseline.Fps,7:0.0} frames/s | {baseline.AllocatedMb,7:0.0} MB managed alloc | native frame allocs {baseline.FrameAllocs} |");
    Console.WriteLine($"| {label} | Skip()           | {current.Fps,7:0.0} frames/s | {current.AllocatedMb,7:0.0} MB managed alloc | native frame allocs {current.FrameAllocs} |");
}

Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// B2. Preprocessing: reused buffers (current) vs allocate-per-frame (naive baseline).
// ---------------------------------------------------------------------------------------------
Console.WriteLine("## B2 — Preprocessing 640x640 tensor: allocate per frame (baseline) vs reused buffers (current), 300 frames");
using (var source = MakeFrame(1920, 1080))
{
    var naive = MeasurePreprocess(source, reuse: false, iterations: 300);
    var reused = MeasurePreprocess(source, reuse: true, iterations: 300);
    Console.WriteLine($"| naive  | {naive.MsPerFrame,6:0.00} ms/frame | {naive.AllocatedKbPerFrame,8:0.0} KB alloc/frame | Gen0 {naive.Gen0} Gen1 {naive.Gen1} Gen2 {naive.Gen2} |");
    Console.WriteLine($"| reused | {reused.MsPerFrame,6:0.00} ms/frame | {reused.AllocatedKbPerFrame,8:0.0} KB alloc/frame | Gen0 {reused.Gen0} Gen1 {reused.Gen1} Gen2 {reused.Gen2} |");
}

Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// B3. Inference latency per model (full Detect(): preprocess + Run + decode + NMS), CPU EP.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("## B3 — Detect() latency on a 1920x1080 frame per device (200 runs after 10 warm-up)");
foreach (var (model, target) in models.SelectMany(m => targets.Select(t => (m, t))))
{
    using var detector = new OnnxDetectorFactory(options).Create(model, target);
    using var frame = FirstFrame(realVideo ?? synthetic);
    for (var i = 0; i < 10; i++)
    {
        detector.Detect(frame);
    }

    var times = new List<double>();
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 200; i++)
    {
        var sw = Stopwatch.StartNew();
        detector.Detect(frame);
        times.Add(sw.Elapsed.TotalMilliseconds);
    }

    var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 200.0 / 1024;
    times.Sort();
    Console.WriteLine($"| {model.DisplayName,-26} | {detector.Info.ExecutionProvider,-50} | input {detector.Info.InputWidth}x{detector.Info.InputHeight} | p50 {Percentile(times, 0.5),6:0.0} ms | p95 {Percentile(times, 0.95),6:0.0} ms | {1000 / Percentile(times, 0.5),5:0.0} inferences/s | {allocated,6:0.0} KB managed alloc/call |");
}

Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// B4. End-to-end analysis pipeline throughput and bounded memory (real model, every frame).
// ---------------------------------------------------------------------------------------------
Console.WriteLine("## B4 — AnalysisJob end to end (bounded channel capacity 4), 450 frames, every frame");
foreach (var (model, target) in models.SelectMany(m => new[] { InferenceTarget.Cpu, DxgiAdapterCatalog.Preferred(targets) }.Distinct().Select(t => (m, t))))
{
    var repo = new InMemoryDetectionRepository();
    repo.Reset(900);
    using var host = new DetectorHost(new OnnxDetectorFactory(options)) { Target = target };
    var detector = await host.GetAsync([model]);
    var job = new AnalysisJob(synthetic, new LimitedDecoderFactory(decoders, 450), detector, repo, new EveryFrameSampling());
    var manager = new ProcessingJobManager();
    var maxLive = 0L;
    var maxQueue = 0;
    var sw = Stopwatch.StartNew();
    manager.Start(job);
    while (!job.Completion.IsCompleted)
    {
        await Task.Delay(20);
        maxLive = Math.Max(maxLive, OwnedVideoFrame.LiveInstances);
        maxQueue = Math.Max(maxQueue, job.GetStatistics().ChannelCount);
    }

    var stats = job.GetStatistics();
    Console.WriteLine($"| {model.DisplayName,-26} | {target,-55} | {stats.FramesAnalyzed / sw.Elapsed.TotalSeconds,6:0.0} frames/s | inference {stats.AverageInferenceTime.TotalMilliseconds,5:0.0} ms | max queue {maxQueue}/4 | max live frames {maxLive} | {job.State} |");
}

Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// B5. Memory soak: playback + analysis concurrently over a long run; working set must be flat.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("## B5 — Memory soak: playback session + analysis (UltraFace, every frame) over 5,400 frames (6 passes)");
{
    var soakModel = models.FirstOrDefault(m => m.Id == OnnxModelCatalog.UltraFaceId) ?? models.First();
    using var host = new DetectorHost(new OnnxDetectorFactory(options));
    var repo = new InMemoryDetectionRepository();
    var samples = new List<(double Seconds, long PrivateMb, long Live)>();
    var process = Process.GetCurrentProcess();
    var sink = new DisposingSink();
    var total = Stopwatch.StartNew();
    for (var pass = 0; pass < 6; pass++)
    {
        repo.Reset(900);
        await using var session = PlaybackSession.Start(decoders.Open(synthetic), sink, TimeProvider.System);
        session.Play();
        var job = new AnalysisJob(synthetic, decoders, await host.GetAsync([soakModel]), repo, new EveryFrameSampling());
        new ProcessingJobManager().Start(job);
        while (!job.Completion.IsCompleted)
        {
            await Task.Delay(500);
            process.Refresh();
            samples.Add((total.Elapsed.TotalSeconds, process.PrivateMemorySize64 / 1_048_576, OwnedVideoFrame.LiveInstances));
        }
    }

    var warm = samples.Skip(samples.Count / 6).ToArray(); // ignore the first pass (model load, arenas, codec init)
    Console.WriteLine($"| samples {samples.Count} over {total.Elapsed.TotalSeconds:0} s | private bytes after warm-up: min {warm.Min(s => s.PrivateMb)} MB, max {warm.Max(s => s.PrivateMb)} MB, last {warm[^1].PrivateMb} MB | max live frames {samples.Max(s => s.Live)} |");
}

Console.WriteLine();

// ---------------------------------------------------------------------------------------------
// B6. Detection repository at 50k and export throughput.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("## B6 — Repository and export");
{
    var repo = new InMemoryDetectionRepository();
    repo.Reset(1_000_000);
    var sw = Stopwatch.StartNew();
    for (var f = 0; f < 25_000; f++)
    {
        repo.AddFrameResults(f, [Detection.Create(f, new NormalizedRect(0.1, 0.1, 0.1, 0.1), 0.9f, ObjectClass.Face), Detection.Create(f, new NormalizedRect(0.5, 0.1, 0.1, 0.1), 0.8f, ObjectClass.Face)]);
    }

    var insert = sw.Elapsed;
    sw.Restart();
    for (var f = 0; f < 100_000; f++)
    {
        _ = repo.GetForDisplay(f % 25_000);
    }

    var lookup = sw.Elapsed;
    sw.Restart();
    var snapshot = repo.Snapshot();
    Console.WriteLine($"| 50,000 detections | insert {insert.TotalMilliseconds:0} ms | 100k display lookups {lookup.TotalMilliseconds:0} ms ({lookup.TotalMicroseconds / 100_000:0.00} µs each) | snapshot {sw.Elapsed.TotalMilliseconds:0} ms ({snapshot.Count:N0}) |");

    var regions = new RedactionRegionStore();
    regions.Add(RedactionRegion(0, 899));
    var export = new VideoExportService(decoders, new EmguVideoWriterFactory(), new RedactionProcessor([new GaussianBlurEffect(), new PixelateEffect(), new SolidBlackEffect()]));
    foreach (var format in new[] { ExportFormat.Mp4, ExportFormat.AviMjpeg })
    {
        var result = await export.ExportAsync(new ExportRequest(synthetic, Path.Combine(work, "export" + (format == ExportFormat.Mp4 ? ".mp4" : ".avi")), format, RedactionSnapshot.Capture(900, new InMemoryDetectionRepository(), regions)));
        Console.WriteLine($"| export {format,-8} | {result.Codec} | {result.FramesWritten} frames in {result.Elapsed.TotalSeconds:0.0} s = {result.FramesWritten / result.Elapsed.TotalSeconds:0.0} frames/s (1080p, blur on every frame) |");
    }
}

// ---------------------------------------------------------------------------------------------
// B7. Blur effect: direct large-kernel Gaussian (baseline) vs reduced-resolution blur (current).
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("## B7 — Gaussian blur of a 576x324 region (strength 0.8), 100 runs");
using (var frame = MakeFrame(1920, 1080))
{
    var roi = new PixelRect(576, 324, 576, 324);
    foreach (var (label, apply) in new (string, Action)[]
    {
        ("direct kernel (baseline)", () => GaussianBlurEffect.DirectBlur(frame, roi, 0.8)),
        ("reduced resolution (current)", () => new GaussianBlurEffect().Apply(frame, roi, 0.8)),
    })
    {
        apply();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            apply();
        }

        Console.WriteLine($"| {label,-30} | {sw.Elapsed.TotalMilliseconds / 100,7:0.00} ms per region |");
    }
}

Directory.Delete(work, recursive: true);
return;

// ---------------------------------------------------------------------------------------------
// B10. Resolution and frame rate: 1080p30 vs 1080p60 vs 4K30 vs 4K60, crowd vs close-up, one video at a time.
// Question: which costs grow with pixels and frame rate, which stay bounded by design (memory), and where the
// bottleneck moves. Every stage uses the app's own classes — EmguVideoDecoderFactory, StoryboardJob, PlaybackSession,
// DetectorHost + AnalysisJob + IouObjectTracker (as App.xaml.cs wires them), VideoExportService with the FFmpeg writer
// over the OpenCV fallback — so the numbers are what the app does, not a re-implementation. One exception: the
// "one reused Mat" decode stage calls OpenCV directly, to separate the per-frame buffer from the colour conversion.
// Method: one unreported warm-up pass (every stage on a short clip, so JIT tiers and DirectML start warm), then N
// measured passes (default 3) with the video order rotated each pass; the summary prints median [min .. max] across
// passes. YOLO26l (the slow model) and the 1-frame-buffer playback run in pass 1 only. Each stage records CPU time of
// this process, of its child processes (ffmpeg.exe, ffprobe.exe) and of the rest of the system; GPU engine use and
// dedicated VRAM per process (PDH "GPU Engine" / "GPU Process Memory"); peak private bytes and the increase over the
// settled level before the stage; and the exact peak of live native frames (OwnedVideoFrame.PeakLiveInstances).
// Before each stage a quiet gate waits (up to 20 s) for other processes to use fewer than 4 logical processors.
// Memory-bounded on purpose: stages never overlap, each one is skipped if less than 8 GiB of physical memory is free,
// every decoder, detector and frame is disposed before the next stage starts, and each export is deleted right after
// its size is read. Console output is also written to docs/blog/_research/b10-output.txt, results to b10-results.json.
// ---------------------------------------------------------------------------------------------
async Task RunCompareAsync(string[] paths, int frameLimit, int repeat)
{
    const long minimumFreeBytes = 8L * 1024 * 1024 * 1024;
    const int warmupFrames = 30, seekCount = 40, seekSeed = 42, warmupPassFrames = 90, leakFrames = 150, leakRuns = 5;
    const double quietBelow = 4.0; // background load (logical processors busy in other processes) a stage waits for
    var quietTimeout = TimeSpan.FromSeconds(20);
    const int exportStageCapacity = 4; // VideoExportService.StageCapacity (private const)
    const double appDefaultRate = 5; // DetectionPreferences: SamplingOptions[3] = "5 frames per second" (TargetRateSampling)
    var stageBudget = TimeSpan.FromMinutes(4);
    var repoRoot = FindRepoRoot();
    var research = Path.Combine(repoRoot, "docs", "blog", "_research");
    Directory.CreateDirectory(research);

    var gpu = DxgiAdapterCatalog.Preferred(targets);

    // The app's export wiring: the FFmpeg writer decorating the OpenCV writer. Its encoder probe runs once, here; the
    // Media Foundation transform it loads is read from FFmpeg's verbose log (hardware or software).
    var writers = new FfmpegVideoWriterFactory(new EmguVideoWriterFactory());
    var encoder = DescribeEncoder(writers);
    var processor = new RedactionProcessor([new GaussianBlurEffect(), new PixelateEffect(), new SolidBlackEffect()]);
    var imaging = new EmguFrameImaging();
    var compareModels = new[] { OnnxModelCatalog.UltraFaceId, OnnxModelCatalog.YoloFaceId, OnnxModelCatalog.Yolo26FaceId }
        .Select(id => models.FirstOrDefault(m => m.Id == id))
        .OfType<DetectionModel>()
        .ToArray();
    var defaultModel = compareModels.FirstOrDefault(m => m.Id == OnnxModelCatalog.YoloFaceId); // DetectionPreferences' default
    var machine = MachineState.Capture();
    using var gpuCounters = GpuCounters.TryCreate(Environment.ProcessId);
    var command = $"dotnet run -c Release --project tools/VisionRedact.Benchmarks -- {string.Join(' ', args)}";
    var keyframesByPath = new Dictionary<string, KeyframeInfo>();

    // Design bounds on live native frames per stage (what the code allows), printed next to the observed peaks.
    var playbackBound = PlaybackOptions.Default.DecodeAheadFrames + 2; // + producer in flight + consumer held (sink disposes at once)
    var aiBound = AnalysisOptions.Default.ChannelCapacity + 2; // + producer in flight + consumer
    const int exportBound = (2 * exportStageCapacity) + 3; // two channels + one frame per stage (decode, redact, encode)

    var console = Console.Out;
    await using var log = new StreamWriter(Path.Combine(research, "b10-output.txt"), append: false, new UTF8Encoding(false)) { AutoFlush = true };
    var tee = new TeeWriter(console, log);
    Console.SetOut(tee);
    try
    {
        var total = Stopwatch.StartNew();
        var files = new List<string>();
        foreach (var input in paths)
        {
            if (ResolveVideo(input, repoRoot) is { } resolved)
            {
                files.Add(resolved);
            }
            else
            {
                Console.WriteLine($"Skipping '{input}': file not found.");
            }
        }

        Console.WriteLine($"# B10 - resolution and frame-rate comparison - {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"Command: {command}");
        Console.WriteLine($"Machine: {machine.LogicalProcessors} logical processors | {machine.TotalRamGib:0.0} GiB RAM | .NET {Environment.Version} | Release={IsRelease()} | AI target: {gpu}");
        Console.WriteLine($"Power: {machine.PowerSource} | power plan: {machine.PowerPlan} | power mode: {machine.PowerMode}");
        Console.WriteLine($"Background load before the run: {machine.BackgroundBusyProcessors:0.0} logical processors busy (1 s sample)");
        Console.WriteLine($"Physical memory free at start: {AvailablePhysicalBytes() / Units.Gib:0.0} GiB (each stage needs >= {minimumFreeBytes / Units.Gib:0.0} GiB free)");
        Console.WriteLine($"Export encoder: FFmpeg {encoder.Selected ?? "not available (OpenCV fallback)"}"
            + (encoder.MftName is null ? "" : $" -> Media Foundation transform '{encoder.MftName}' {encoder.MftClsid} ({(encoder.MftIsHardware == true ? "hardware" : "software, runs on CPU cores")})")
            + (encoder.NvencProbe is null ? "" : $" | NVENC probe: {encoder.NvencProbe}"));
        Console.WriteLine($"Models: {string.Join(", ", compareModels.Select(m => $"{ShortName(m)} (confidence {m.DefaultConfidence})"))}; NMS IoU {options.NmsIouThreshold}; minimum box {options.MinimumBoxSize}; tracker: IouObjectTracker() as App.xaml.cs configures it");
        Console.WriteLine($"AI sampling: each model's main run analyzes every frame (a stress test). The app's default is {appDefaultRate:0} analyzed frames per second (TargetRateSampling) with YOLO11n; that is measured as its own stage.");
        Console.WriteLine($"Frame limit: the first {frameLimit:N0} frames of each video, in every stage. Seek positions: Random({seekSeed}), {seekCount} seeks.");
        Console.WriteLine($"Passes: {repeat} measured, video order rotated each pass, after one unreported warm-up pass. YOLO26l and the 1-frame-buffer playback run in pass 1 only. Summary cells: median [min .. max] across passes.");
        Console.WriteLine($"Sampling inside a stage: process and child-process memory every 100 ms; new child processes and GPU counters every 500 ms ({(gpuCounters is null ? "GPU counters not available" : "PDH")}); CPU from process and system times (exact).");
        Console.WriteLine($"Quiet gate: before each stage the harness waits (up to {quietTimeout.TotalSeconds:0} s) until other processes use fewer than {quietBelow:0.0} logical processors over 0.5 s; the level at stage start is recorded.");
        Console.WriteLine("Units: MiB = 1,048,576 bytes, KiB = 1,024 bytes, GiB = 1,073,741,824 bytes; Mpx = million pixels; values are rounded to the digits shown.");
        Console.WriteLine();

        foreach (var file in files.Distinct())
        {
            keyframesByPath[file] = ProbeKeyframes(writers.FfprobePath, file, frameLimit);
        }

        if (files.Count > 0)
        {
            var warmupClock = Stopwatch.StartNew();
            var warmupLimit = Math.Min(warmupPassFrames, frameLimit);
            Console.WriteLine($"Warm-up pass (not reported): every stage and every model on the first {warmupLimit} frames of {Path.GetFileName(files[0])}.");
            Console.SetOut(TextWriter.Null);
            try
            {
                _ = await RunVideoAsync(files[0], warmupLimit, 0, firstPass: true);
            }
            finally
            {
                Console.SetOut(tee);
            }

            Console.WriteLine($"Warm-up pass done in {warmupClock.Elapsed.TotalSeconds:0.0} s.");
            Console.WriteLine();
        }

        var reports = new List<VideoReport>();
        for (var pass = 1; pass <= repeat; pass++)
        {
            var shift = (pass - 1) % Math.Max(1, files.Count);
            var order = files.Skip(shift).Concat(files.Take(shift)).ToArray();
            Console.WriteLine($"# Pass {pass} of {repeat}: {string.Join(", ", order.Select(p => Path.GetFileNameWithoutExtension(p)))}");
            Console.WriteLine();
            foreach (var path in order)
            {
                reports.Add(await RunVideoAsync(path, frameLimit, pass, firstPass: pass == 1));
            }
        }

        List<LeakRow> leak = files.Count == 0 ? [] : await LeakCheckAsync(files[0], Math.Min(leakFrames, frameLimit));

        PrintCompareTables(reports, files, frameLimit, playbackBound, aiBound, exportBound);
        Console.WriteLine();
        Console.WriteLine($"### Leak check: each model {leakRuns} times in one process (every frame of a short clip), private bytes after a settled GC");
        Console.WriteLine($"| model | frames per run | before | {string.Join(" | ", Enumerable.Range(1, leakRuns).Select(i => $"after run {i}"))} | run 1 change | runs 1 -> {leakRuns} change |");
        Console.WriteLine("|---|---|---|" + string.Concat(Enumerable.Repeat("---|", leakRuns)) + "---|---|");
        foreach (var row in leak)
        {
            var after = row.AfterMib.Select(a => $"{a:0} MiB").Concat(Enumerable.Repeat("n/a", Math.Max(0, leakRuns - row.AfterMib.Length))).ToArray();
            var first = row.AfterMib.Length > 0 ? $"{row.AfterMib[0] - row.BeforeMib:+0;-0;0} MiB" : "n/a";
            var rest = row.AfterMib.Length > 1 ? $"{row.AfterMib[^1] - row.AfterMib[0]:+0;-0;0} MiB" : "n/a";
            Console.WriteLine($"| {row.Model} | {row.Frames} | {row.BeforeMib:0} MiB | {string.Join(" | ", after)} | {first} | {rest} |");
        }

        Console.WriteLine();
        Console.WriteLine($"Total runtime: {total.Elapsed:hh\\:mm\\:ss} ({total.Elapsed.TotalMinutes:0.0} min)");

        var json = JsonSerializer.Serialize(
            new
            {
                Benchmark = "B10",
                Generated = DateTime.Now,
                Command = command,
                Machine = machine,
                Runtime = Environment.Version.ToString(),
                AiTarget = gpu.ToString(),
                Encoder = encoder,
                Settings = new
                {
                    FrameLimit = frameLimit,
                    Passes = repeat,
                    WarmupPassFrames = Math.Min(warmupPassFrames, frameLimit),
                    WarmupFramesPerDecodeStage = warmupFrames,
                    SeekCount = seekCount,
                    SeekSeed = seekSeed,
                    AppDefaultAnalyzedFramesPerSecond = appDefaultRate,
                    options.NmsIouThreshold,
                    options.MinimumBoxSize,
                    Confidence = compareModels.ToDictionary(ShortName, m => m.DefaultConfidence),
                    LiveFrameBounds = new { Decode = 1, Playback = playbackBound, Ai = aiBound, Export = exportBound },
                },
                TotalSeconds = total.Elapsed.TotalSeconds,
                Videos = reports,
                LeakCheck = leak,
            },
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(research, "b10-results.json"), json);
        Console.WriteLine($"Results: {Path.Combine(research, "b10-results.json")}");
    }
    finally
    {
        Console.SetOut(console);
    }

    // One video, every stage in order. pass 0 = the unreported warm-up pass.
    async Task<VideoReport> RunVideoAsync(string path, int limit, int pass, bool firstPass)
    {
        // Every stage reads through this factory: it stops each decoder after the frame limit and reports the
        // shortened frame count, so plans (storyboard, analysis totals, export) cover exactly the frames used.
        var clip = new LimitedDecoderFactory(decoders, limit);
        var report = new VideoReport { File = Path.GetFileName(path), Pass = pass };

        // 1. Metadata. Bytes per decoded frame = what one OwnedVideoFrame costs in native memory (BGR, 3 bytes/pixel).
        using (var probe = decoders.Open(path))
        {
            var m = probe.Metadata;
            (report.Width, report.Height, report.Fps, report.ContainerFrames) = (m.Width, m.Height, m.FramesPerSecond, m.FrameCount);
            (report.DurationSeconds, report.Codec, report.Backend, report.FileBytes) = (m.Duration.TotalSeconds, m.Codec, probe.BackendName, m.FileSizeBytes);
            report.FramesUsed = Math.Min(m.FrameCount, limit);
            report.BytesPerFrame = (long)m.Width * m.Height * 3;
        }

        report.Keyframes = keyframesByPath.GetValueOrDefault(path);
        Settle();
        report.PrivateStartMib = SelfPrivateBytes() / Units.Mib;
        var gop = report.Keyframes is { Count: > 0 } k ? $"keyframes in the frames used: {k.Count} (GOP median {k.MedianGopFrames?.ToString("0") ?? $">= {report.FramesUsed}"} frames)" : "keyframes: n/a";
        Console.WriteLine($"## {report.File} (pass {pass}): {report.Width}x{report.Height} @ {report.Fps:0.###} fps, {report.ContainerFrames:N0} frames ({report.DurationSeconds:0.0} s), {report.Codec} via {report.Backend}; using {report.FramesUsed:N0} frames; {report.BytesPerFrame / Units.Mib:0.0} MiB per decoded BGR frame; {gop}");

        // 2. Sequential decode (ReadNext = demux + decode + BGR conversion into a new native frame), after 30 warm-up
        // frames. Headroom = decode fps / video fps: below 1.0, one decode thread cannot keep up with real time.
        // CPU = process CPU time during the timed loop only / its wall time (logical processors busy on average).
        await Stage(report, "decode", 1, () =>
        {
            using var decoder = clip.Open(path);
            for (var i = 0; i < warmupFrames; i++)
            {
                using var warm = decoder.ReadNext();
            }

            var times = new List<double>((int)report.FramesUsed);
            var cpu = SelfCpu();
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var started = Stopwatch.GetTimestamp();
                using var frame = decoder.ReadNext();
                if (frame is null)
                {
                    break;
                }

                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            var wall = sw.Elapsed.TotalSeconds;
            var cores = (SelfCpu() - cpu).TotalSeconds / wall;
            var fps = times.Count / wall;
            var mean = times.Average();
            times.Sort();
            report.Decode = new DecodeResult(times.Count, fps, mean, Percentile(times, 0.5), Percentile(times, 0.99), times[^1], fps / report.Fps, cores);
            Console.WriteLine($"| decode (ReadNext) | {times.Count} frames | {fps,7:0.0} frames/s | {mean,6:0.00} ms/frame (p50 {Percentile(times, 0.5):0.00}, p99 {Percentile(times, 0.99):0.00}, max {times[^1]:0.0}) | headroom {fps / report.Fps:0.00}x real time | {cores:0.0} logical processors busy |");
            return Task.FromResult<string?>(null);
        });

        // 2b. The same walk with Skip() (grab without retrieve: decode, no BGR conversion, no new frame).
        await Stage(report, "decode (grab only)", 0, () =>
        {
            using var decoder = clip.Open(path);
            for (var i = 0; i < warmupFrames; i++)
            {
                decoder.Skip();
            }

            var frames = 0;
            var cpu = SelfCpu();
            var sw = Stopwatch.StartNew();
            while (decoder.Skip())
            {
                frames++;
            }

            var wall = sw.Elapsed.TotalSeconds;
            var cores = (SelfCpu() - cpu).TotalSeconds / wall;
            report.Grab = new GrabResult(frames, frames / wall, 1000 * wall / frames, cores);
            Console.WriteLine($"| decode (Skip, grab only) | {frames} frames | {frames / wall,7:0.0} frames/s | {1000 * wall / frames,6:0.00} ms/frame | {cores:0.0} logical processors busy |");
            return Task.FromResult<string?>(null);
        });

        // 2c. Grab + retrieve into ONE reused Mat, straight on OpenCV with the same FFmpeg backend the app's decoder
        // uses. Same colour conversion as ReadNext, but no new 3-bytes-per-pixel buffer (allocation, first-touch page
        // faults, free) per frame. ReadNext minus this = cost of the fresh buffer; this minus grab = the conversion.
        await Stage(report, "decode (one reused Mat)", 0, () =>
        {
            using var capture = new VideoCapture(path, VideoCapture.API.Ffmpeg);
            using var mat = new Mat();
            long position = 0;
            for (; position < warmupFrames && capture.Read(mat); position++)
            {
            }

            var times = new List<double>((int)report.FramesUsed);
            var cpu = SelfCpu();
            var sw = Stopwatch.StartNew();
            while (position < report.FramesUsed)
            {
                var started = Stopwatch.GetTimestamp();
                if (!capture.Read(mat) || mat.IsEmpty)
                {
                    break;
                }

                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                position++;
            }

            var wall = sw.Elapsed.TotalSeconds;
            var cores = (SelfCpu() - cpu).TotalSeconds / wall;
            var mean = times.Average();
            times.Sort();
            report.Reused = new ReusedMatResult(times.Count, times.Count / wall, mean, Percentile(times, 0.99), cores);
            Console.WriteLine($"| decode (Read into one reused Mat) | {times.Count} frames | {times.Count / wall,7:0.0} frames/s | {mean,6:0.00} ms/frame (p99 {Percentile(times, 0.99):0.00}) | {cores:0.0} logical processors busy |");
            return Task.FromResult<string?>(null);
        });

        // 3. Random seek latency: what the hover preview's exact frame costs (seek to the keyframe before the
        // target, decode forward to it). 40 seeks with a fixed seed, so every video is asked the same way.
        await Stage(report, "seek", 1, () =>
        {
            using var decoder = clip.Open(path);
            var random = new Random(seekSeed);
            var times = new List<double>(seekCount);
            for (var i = 0; i < seekCount; i++)
            {
                var target = random.NextInt64(report.FramesUsed);
                var started = Stopwatch.GetTimestamp();
                decoder.Seek(target);
                using var frame = decoder.ReadNext();
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            var mean = times.Average();
            times.Sort();
            report.Seek = new SeekResult(seekCount, seekSeed, Percentile(times, 0.5), Percentile(times, 0.9), times[^1], mean);
            Console.WriteLine($"| seek + ReadNext | {seekCount} random frames | median {Percentile(times, 0.5),6:0.0} ms | p90 {Percentile(times, 0.9),6:0.0} ms | max {times[^1],6:0.0} ms |");
            return Task.FromResult<string?>(null);
        });

        // 4. Storyboard: the app's StoryboardJob (own decoder, coarse-to-fine plan, one native frame at a time).
        await Stage(report, "storyboard", 1, async () =>
        {
            var board = Storyboard.Empty;
            var job = new StoryboardJob(path, clip, imaging, FramePreviewOptions.Default, tile => board = board.With(tile));
            new ProcessingJobManager().Start(job);
            await job.Completion;
            if (job.State != ProcessingJobState.Completed)
            {
                throw new InvalidOperationException($"Storyboard {job.State}: {job.Error?.Message}");
            }

            var first = board.Nearest(0);
            report.Storyboard = new StoryboardResult(board.Count, job.TilesPlanned, job.Elapsed.TotalSeconds, board.PixelBytes, first?.Image.Width ?? 0, first?.Image.Height ?? 0, job.PeakFramesHeld);
            Console.WriteLine($"| storyboard | {board.Count}/{job.TilesPlanned} tiles of {first?.Image.Width}x{first?.Image.Height} | {job.Elapsed.TotalSeconds,5:0.00} s | {board.PixelBytes / Units.Kib:0} KiB tiles | peak native frames held {job.PeakFramesHeld} |");
            return null;
        });

        // 5. Real-time playback: the app's PlaybackSession (one decode thread, decode-ahead buffer, clock pacing, late
        // frames dropped). The sink records when each frame is presented and disposes it at once, so the WPF bitmap
        // upload is not included. Lateness = present time minus due time (anchor + frames / fps), the clock's anchor
        // being the first frame presented after Play. Pass 1 repeats it with a 1-frame buffer to show what the buffer does.
        await Stage(report, "playback", playbackBound, () => PlaybackAsync(PlaybackOptions.Default));
        if (firstPass)
        {
            await Stage(report, "playback (1-frame buffer)", 1 + 2, () => PlaybackAsync(PlaybackOptions.Default with { DecodeAheadFrames = 1 }));
        }

        // 6. AI scan per model on the GPU, through DetectorHost + AnalysisJob exactly as AnalysisService runs it:
        // bounded channel (4), the model's default confidence, a fresh IouObjectTracker per run, every frame. If the
        // estimate from the warm-up runs says a run would exceed 4 minutes, it analyzes 3x fewer frames and says so.
        InMemoryDetectionRepository? yoloDetections = null;
        foreach (var model in compareModels)
        {
            if (!firstPass && model.Id == OnnxModelCatalog.Yolo26FaceId)
            {
                continue;
            }

            var stage = "AI " + ShortName(model);
            await Stage(report, stage, aiBound, async () =>
            {
                var (result, repository) = await RunAiAsync(path, clip, report, model, stage, appDefault: false);
                report.Ai.Add(result);
                if (model.Id == OnnxModelCatalog.YoloFaceId)
                {
                    yoloDetections = repository;
                }

                PrintAi(result);
                return result.Reduction;
            });
        }

        // 6b. What the app does by default: YOLO11n at 5 analyzed frames per second of video (the other frames are
        // skipped with Skip(), without retrieving pixels).
        if (defaultModel is not null)
        {
            var stage = $"AI YOLO11n @ {appDefaultRate:0}/s";
            await Stage(report, stage, aiBound, async () =>
            {
                var (result, _) = await RunAiAsync(path, clip, report, defaultModel, stage, appDefault: true);
                report.Ai.Add(result);
                PrintAi(result);
                return null;
            });
        }

        // 7. Export as the app does it: decode || redact || encode, FFmpeg h264_mf (+ AAC when the source has audio),
        // the YOLO11n every-frame detections of step 6 blurred with the default settings (Gaussian, 20 % padding).
        // Steady rate = frames handed to the encoder between 10 % and 90 % of the export, from the progress reports
        // (excludes ffprobe, the ffmpeg start, the encoder flush and the +faststart rewrite that the total includes).
        await Stage(report, "export", exportBound, async () =>
        {
            if (yoloDetections is null)
            {
                return "skipped: no YOLO11n detections";
            }

            var exporter = new VideoExportService(clip, writers, processor);
            var output = Path.Combine(work, Path.GetFileNameWithoutExtension(path) + "-redacted.mp4");
            var progress = new ProgressRecorder();
            try
            {
                var snapshot = RedactionSnapshot.Capture(report.FramesUsed, yoloDetections, new RedactionRegionStore());
                var result = await exporter.ExportAsync(new ExportRequest(path, output, ExportFormat.Mp4, snapshot), progress);
                var mib = new FileInfo(output).Length / Units.Mib;
                var fps = result.FramesWritten / result.Elapsed.TotalSeconds;
                var steady = progress.SteadyFramesPerSecond(result.FramesWritten);
                report.Export = new ExportRow(result.Codec, result.FramesWritten, result.FramesRedacted, result.Elapsed.TotalSeconds, fps, steady, mib, result.AudioPreserved);
                Console.WriteLine($"| export | {result.Codec} | {result.FramesWritten} frames ({result.FramesRedacted} with blur) in {result.Elapsed.TotalSeconds:0.0} s = {fps,6:0.0} frames/s total, {steady?.ToString("0.0") ?? "n/a"} frames/s steady (10-90 %) | {mib:0.0} MiB | audio {(result.AudioPreserved ? "yes" : "no")} |");
            }
            finally
            {
                // Disk is limited: the export exists only long enough to read its size.
                if (File.Exists(output))
                {
                    File.Delete(output);
                }
            }

            return null;
        });

        Settle();
        report.PrivateEndMib = SelfPrivateBytes() / Units.Mib;
        Console.WriteLine($"| settled private bytes, start -> end of this video | {report.PrivateStartMib:0} -> {report.PrivateEndMib:0} MiB |");
        Console.WriteLine();
        return report;

        async Task<string?> PlaybackAsync(PlaybackOptions playback)
        {
            var sink = new TimingSink();
            await using var session = PlaybackSession.Start(clip.Open(path), sink, TimeProvider.System, options: playback);
            while (session.State == PlaybackState.Starting)
            {
                await Task.Delay(5);
            }

            var clipSeconds = report.FramesUsed / report.Fps;
            var played = Stopwatch.GetTimestamp();
            var sw = Stopwatch.StartNew();
            session.Play();
            while (session.State is not (PlaybackState.Ended or PlaybackState.Faulted) && sw.Elapsed.TotalSeconds < (clipSeconds * 4) + 10)
            {
                await Task.Delay(20);
            }

            var wall = sw.Elapsed.TotalSeconds;
            var s = session.GetStatistics();
            var late = sink.Lateness(played, report.Fps);
            // Presented fps counts the frames presented after Play (the paused first frame is not part of the playback).
            var presentedFps = late.Count > 0 ? (late.Count + 1) / wall : s.FramesPresented / wall;
            var result = new PlaybackResult(playback.DecodeAheadFrames, clipSeconds, wall, s.FramesDecoded, s.FramesPresented, s.FramesDroppedLate, presentedFps,
                s.AverageDecodeTime.TotalMilliseconds, session.State.ToString(), late.Count, late.P50, late.P99, late.Max, late.OverHalfFrame);
            if (playback.DecodeAheadFrames == PlaybackOptions.Default.DecodeAheadFrames)
            {
                report.Playback = result;
            }
            else
            {
                report.PlaybackOneFrameBuffer = result;
            }

            Console.WriteLine($"| playback, {playback.DecodeAheadFrames}-frame buffer | {session.State} after {wall:0.0} s for a {clipSeconds:0.0} s clip | presented {s.FramesPresented} ({late.Count + 1} after Play) = {result.PresentedFps:0.0} fps of {report.Fps:0.##} | late drops {s.FramesDroppedLate} | lateness p50 {late.P50:0.0} / p99 {late.P99:0.0} / max {late.Max:0.0} ms, {late.OverHalfFrame} presented > half a frame late | decode {s.AverageDecodeTime.TotalMilliseconds:0.0} ms avg |");
            return null;
        }
    }

    void PrintAi(AiResult r) =>
        Console.WriteLine($"| {r.Stage,-16} | {r.Sampling,-20} | {r.FramesAnalyzed,5} frames in {r.Seconds,6:0.0} s = {r.AnalyzedPerSecond,6:0.0} analyzed/s, {r.RealTimeFactor:0.00}x real time | Detect {r.MeanDetectMs,5:0.0} ms | {r.Detections,6:N0} faces, {r.MeanFaces:0.0} avg / {r.MaxFaces} max per frame | {r.Tracks,4} tracks | queue max {r.MaxQueue}/{AnalysisOptions.Default.ChannelCapacity}, live frames peak {r.MaxLiveFrames} | load+warm-up {r.LoadSeconds:0.0} s |{(r.Reduction is null ? "" : " REDUCED: " + r.Reduction + " |")}");

    // One AI run as the app does it. Wall time ends on a continuation of job.Completion (no polling delay) and
    // includes opening the job's own decoder.
    async Task<(AiResult Result, InMemoryDetectionRepository Repository)> RunAiAsync(string path, IVideoDecoderFactory clip, VideoReport report, DetectionModel model, string stage, bool appDefault)
    {
        using var host = new DetectorHost(new OnnxDetectorFactory(options)) { Target = gpu };
        var load = Stopwatch.StartNew();
        var detector = await host.GetAsync([model]);

        // Warm-up on frame 0: the first DirectML runs compile shaders and size memory arenas. Median of the last 3 of 5
        // runs feeds the time estimate.
        var warmRuns = new List<double>();
        using (var decoder = clip.Open(path))
        using (var first = decoder.ReadNext() ?? throw new InvalidOperationException("No frame."))
        {
            for (var i = 0; i < 5; i++)
            {
                var started = Stopwatch.GetTimestamp();
                detector.Detect(first);
                warmRuns.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        var loadSeconds = load.Elapsed.TotalSeconds;
        var warmMs = warmRuns.Skip(2).Order().ElementAt(1);
        IFrameSamplingStrategy sampling;
        string? reduction = null;
        if (appDefault)
        {
            sampling = new TargetRateSampling(appDefaultRate, report.Fps);
        }
        else
        {
            var decodeMs = report.Decode?.MsPerFrame ?? 0;
            double Estimate(int every) => Math.Max(Math.Ceiling(report.FramesUsed / (double)every) * warmMs, report.FramesUsed * decodeMs) / 1000;
            var interval = 1;
            if (Estimate(1) > stageBudget.TotalSeconds)
            {
                interval = 3;
                reduction = $"estimated {Estimate(1):0} s for every frame (> {stageBudget.TotalMinutes:0} min): analyzed 1 of every 3 frames instead";
            }

            sampling = interval == 1 ? new EveryFrameSampling() : new EveryNthFrameSampling(interval);
        }

        var repository = new InMemoryDetectionRepository();
        repository.Reset(report.FramesUsed);
        var job = new AnalysisJob(path, clip, detector, repository, sampling, new IouObjectTracker());
        var maxQueue = 0;
        var sw = Stopwatch.StartNew();
        new ProcessingJobManager().Start(job);
        while (!job.Completion.IsCompleted)
        {
            // Wakes as soon as the job completes; meanwhile samples the queue fill every 20 ms.
            await Task.WhenAny(job.Completion, Task.Delay(20));
            maxQueue = Math.Max(maxQueue, job.GetStatistics().ChannelCount);
        }

        var seconds = sw.Elapsed.TotalSeconds;
        if (job.State != ProcessingJobState.Completed)
        {
            throw new InvalidOperationException($"Analysis {job.State}: {job.Error?.Message}");
        }

        var stats = job.GetStatistics();
        var perFrame = repository.AnalyzedFrames().Select(f => repository.GetForFrame(f).Count).ToArray();
        var tracks = repository.Snapshot().Select(d => d.TrackId).OfType<int>().Distinct().Count();
        var result = new AiResult(stage, ShortName(model), detector.Info.ExecutionProvider, detector.Info.InputWidth, sampling.Description, reduction,
            stats.FramesAnalyzed, seconds, stats.FramesAnalyzed / seconds, report.FramesUsed / report.Fps / seconds, stats.AverageInferenceTime.TotalMilliseconds, stats.Detections,
            perFrame.Length == 0 ? 0 : perFrame.Average(), perFrame.Length == 0 ? 0 : perFrame.Max(), tracks, maxQueue, OwnedVideoFrame.PeakLiveInstances, loadSeconds, warmMs);
        return (result, repository);
    }

    // Is the memory an AI run leaves behind a leak? Each model runs leakRuns times in this process; if the settled
    // private bytes level off after the first run, the growth is one-time (runtime/driver caches), not per run.
    async Task<List<LeakRow>> LeakCheckAsync(string path, int frames)
    {
        var rows = new List<LeakRow>();
        var clip = new LimitedDecoderFactory(decoders, frames);
        var report = new VideoReport { File = Path.GetFileName(path) };
        using (var probe = clip.Open(path))
        {
            (report.Fps, report.FramesUsed, report.ContainerFrames) = (probe.Metadata.FramesPerSecond, probe.Metadata.FrameCount, probe.Metadata.FrameCount);
        }

        Console.WriteLine($"## Leak check: each model {leakRuns} times on the first {report.FramesUsed} frames of {report.File}, every frame; settled private bytes after each run");
        foreach (var model in compareModels)
        {
            var free = AvailablePhysicalBytes();
            if (free < minimumFreeBytes)
            {
                Console.WriteLine($"| leak check {ShortName(model)} | SKIPPED: only {free / Units.Gib:0.0} GiB physical memory free |");
                continue;
            }

            Settle();
            var before = SelfPrivateBytes() / Units.Mib;
            var after = new List<double>();
            for (var run = 0; run < leakRuns; run++)
            {
                try
                {
                    _ = await RunAiAsync(path, clip, report, model, "leak check", appDefault: false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or MediaException or DetectionException)
                {
                    Console.WriteLine($"| leak check {ShortName(model)} | FAILED: {ex.Message} |");
                    break;
                }

                Settle();
                after.Add(SelfPrivateBytes() / Units.Mib);
            }

            rows.Add(new LeakRow(ShortName(model), report.FramesUsed, before, [.. after]));
            Console.WriteLine($"| leak check {ShortName(model),-9} | before {before:0} MiB | after each run: {string.Join(" / ", after.Select(a => a.ToString("0")))} MiB |");
        }

        Console.WriteLine();
        return rows;
    }

    // Runs one stage alone: memory guard, settled GC, then a sampler for peak private bytes / working set of this
    // process and its children, CPU of this process / children / everything else, GPU engine use and VRAM; the exact
    // peak of live native frames; private bytes after a settled GC.
    async Task Stage(VideoReport report, string name, long liveBound, Func<Task<string?>> body)
    {
        var free = AvailablePhysicalBytes();
        if (free < minimumFreeBytes)
        {
            Console.WriteLine($"| {name} | SKIPPED: only {free / Units.Gib:0.0} GiB physical memory free (needs {minimumFreeBytes / Units.Gib:0.0} GiB) |");
            report.Stages.Add(new StageMemory { Stage = name, Note = $"skipped: {free / Units.Gib:0.0} GiB free" });
            return;
        }

        var (background, waited) = await WaitForQuietAsync();
        Settle();
        var before = SelfPrivateBytes();
        OwnedVideoFrame.ResetPeakLiveInstances();
        string? note;
        StageSample sample;
        using (var sampler = new ProcessSampler(gpuCounters))
        {
            try
            {
                note = await body();
            }
            catch (Exception ex) when (ex is InvalidOperationException or MediaException or DetectionException or ExportException or IOException)
            {
                note = $"failed: {ex.GetType().Name}: {ex.Message}";
                Console.WriteLine($"| {name} | FAILED: {ex.Message} |");
            }

            sample = sampler.Stop();
        }

        var peakLive = OwnedVideoFrame.PeakLiveInstances;
        Settle();
        var after = SelfPrivateBytes();
        var stage = new StageMemory
        {
            Stage = name,
            Seconds = sample.Seconds,
            PrivateBeforeMib = before / Units.Mib,
            PrivateAfterMib = after / Units.Mib,
            PeakPrivateMib = Math.Max(sample.PeakPrivate, before) / Units.Mib,
            PeakWorkingSetMib = sample.PeakWorkingSet / Units.Mib,
            PeakLiveFrames = peakLive,
            LiveFrameBound = liveBound,
            AppCores = sample.AppCores,
            ChildCores = sample.ChildCores,
            ChildProcesses = sample.ChildNames,
            ChildPeakPrivateMib = sample.ChildPeakCommit / Units.Mib,
            SystemCores = sample.SystemCores,
            GpuAppPercent = sample.GpuAppPercent,
            GpuOthersPercent = sample.GpuOthersPercent,
            VramBeforeMib = sample.VramFirst / Units.Mib,
            VramPeakMib = sample.VramPeak / Units.Mib,
            SamplerMsPerSecond = sample.SamplerMsPerSecond,
            BackgroundAtStart = background,
            QuietWaitSeconds = waited,
            Note = note,
        };
        report.Stages.Add(stage);
        var child = stage.ChildProcesses.Length == 0 ? "" : $", child {stage.ChildCores:0.0} ({stage.ChildProcesses}, peak private {stage.ChildPeakPrivateMib:0} MiB)";
        var gpuText = stage.GpuAppPercent is null ? "n/a" : $"app {stage.GpuAppPercent:0} %, others {stage.GpuOthersPercent:0} %";
        var vram = stage.VramPeakMib is null ? "n/a" : $"{stage.VramBeforeMib:0} -> peak {stage.VramPeakMib:0} MiB";
        Console.WriteLine($"|   {name,-26} | {stage.Seconds,6:0.0} s | private {stage.PrivateBeforeMib,5:0} -> {stage.PrivateAfterMib,5:0} MiB, peak {stage.PeakPrivateMib,5:0} (+{stage.IncreaseMib:0}) | working set peak {stage.PeakWorkingSetMib,5:0} MiB | live frames peak {peakLive} (bound {liveBound}) | CPU: app {stage.AppCores:0.0}{child}, others {stage.OtherCores:0.0} (at start {background:0.0}{(waited > 0.6 ? $", waited {waited:0} s" : "")}) | GPU: {gpuText} | VRAM: {vram} |");
    }

    // Waits until other processes are quiet (fewer than quietBelow logical processors busy over 0.5 s, this process
    // excluded), at most quietTimeout. Returns the last background level and the time waited.
    async Task<(double Background, double Waited)> WaitForQuietAsync()
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var system = SystemCpu.Times();
            var self = SelfCpu();
            var window = Stopwatch.StartNew();
            await Task.Delay(500);
            var seconds = window.Elapsed.TotalSeconds;
            var others = SystemCpu.BusyProcessors(system, SystemCpu.Times(), seconds) - ((SelfCpu() - self).TotalSeconds / seconds);
            if (others < quietBelow || clock.Elapsed >= quietTimeout)
            {
                return (others, clock.Elapsed.TotalSeconds);
            }
        }
    }
}

// The B10 summary: one row per metric, one column per video, so 1080p/4K and 30/60 fps read side by side. Values
// measured in every pass print as median [min .. max]; detection counts print from pass 1.
static void PrintCompareTables(List<VideoReport> reports, List<string> files, int frameLimit, int playbackBound, int aiBound, int exportBound)
{
    var names = files.Distinct().Select(f => Path.GetFileName(f)).ToArray();
    var header = "| metric | " + string.Join(" | ", names.Select(n => Path.GetFileNameWithoutExtension(n))) + " |";
    var rule = "|---|" + string.Concat(names.Select(_ => "---|"));
    var passes = reports.Select(r => r.Pass).Distinct().Count();

    void Table(string title, string? note = null)
    {
        Console.WriteLine();
        Console.WriteLine($"### {title}");
        if (note is not null)
        {
            Console.WriteLine(note);
        }

        Console.WriteLine(header);
        Console.WriteLine(rule);
    }

    void Row(string metric, Func<List<VideoReport>, string?> cell) =>
        Console.WriteLine($"| {metric} | " + string.Join(" | ", names.Select(n => reports.Where(r => r.File == n).OrderBy(r => r.Pass).ToList()).Select(rs => (rs.Count == 0 ? null : cell(rs)) ?? "n/a")) + " |");

    // Median [min .. max] of the passes that have a value; a single value prints alone.
    static string? Agg(IEnumerable<double?> values, string format)
    {
        var v = values.OfType<double>().Where(double.IsFinite).Order().ToArray();
        if (v.Length == 0)
        {
            return null;
        }

        var median = v.Length % 2 == 1 ? v[v.Length / 2] : (v[(v.Length / 2) - 1] + v[v.Length / 2]) / 2;
        return v.Length == 1 ? median.ToString(format) : $"{median.ToString(format)} [{v[0].ToString(format)} .. {v[^1].ToString(format)}]";
    }

    static string? A(List<VideoReport> rs, Func<VideoReport, double?> value, string format, string suffix = "") => Agg(rs.Select(value), format) is { } s ? s + suffix : null;
    static StageMemory? S(VideoReport r, string stage) => r.Stages.FirstOrDefault(s => s.Stage == stage && s.Seconds > 0);

    Console.WriteLine($"## B10 summary ({passes} measured pass{(passes == 1 ? "" : "es")}; cells: median [min .. max] across passes unless marked pass 1)");

    Table("Source");
    Row("resolution", rs => $"{rs[0].Width}x{rs[0].Height}");
    Row("frame rate", rs => $"{rs[0].Fps:0.##} fps");
    Row("frames (container) / used", rs => rs[0].FramesUsed < rs[0].ContainerFrames ? $"{rs[0].ContainerFrames:N0} / first {rs[0].FramesUsed:N0}" : $"{rs[0].ContainerFrames:N0} / all");
    Row("duration", rs => rs[0].DurationSeconds >= 60 ? $"{TimeSpan.FromSeconds(rs[0].DurationSeconds):m\\:ss} (used {rs[0].FramesUsed / rs[0].Fps:0.0} s)" : $"{rs[0].DurationSeconds:0.0} s");
    Row("average file bitrate (whole file)", rs => rs[0].DurationSeconds > 0 ? $"{rs[0].FileBytes * 8 / rs[0].DurationSeconds / 1e6:0.0} Mbit/s" : null);
    Row("bytes per decoded BGR frame (w*h*3)", rs => $"{rs[0].BytesPerFrame / Units.Mib:0.0} MiB ({rs[0].BytesPerFrame:N0} B)");
    Row("pixels per second of video", rs => $"{rs[0].Width * (double)rs[0].Height * rs[0].Fps / 1e6:0} Mpx/s");
    Row("keyframes in the frames used (ffprobe)", rs => rs[0].Keyframes is { Count: > 0 } k ? $"{k.Count}" : rs[0].Keyframes?.Error);
    Row("keyframe interval (GOP), frames: median / max", rs => rs[0].Keyframes is { MedianGopFrames: { } med } k ? $"{med:0} / {k.MaxGopFrames:0}" : rs[0].Keyframes is { Count: 1 } ? $"one keyframe (GOP >= {rs[0].FramesUsed} frames)" : null);

    Table("Decode (one decode thread, as PlaybackSession uses it)", "CPU = this process's CPU time during the timed loop / its wall time (logical processors busy on average; 22 on this CPU).");
    Row("ReadNext: frames/s", rs => A(rs, r => r.Decode?.FramesPerSecond, "0.0"));
    Row("ReadNext: ms per frame, mean", rs => A(rs, r => r.Decode?.MsPerFrame, "0.00"));
    Row("ReadNext: ms per frame, p99", rs => A(rs, r => r.Decode?.P99Ms, "0.0"));
    Row("ReadNext: ms per frame, max", rs => A(rs, r => r.Decode?.MaxMs, "0.0"));
    Row("real-time headroom (ReadNext fps / video fps)", rs => A(rs, r => r.Decode?.Headroom, "0.00", "x"));
    Row("ReadNext keeps up with real time in every pass?", rs => rs.Where(r => r.Decode is not null).Select(r => r.Decode!.Headroom).DefaultIfEmpty(double.NaN).Min() is var min && double.IsFinite(min) ? (min >= 1 ? $"yes (lowest {min:0.00}x)" : $"NO (lowest {min:0.00}x)") : null);
    Row("grab only, Skip(): frames/s", rs => A(rs, r => r.Grab?.FramesPerSecond, "0.0"));
    Row("grab only: ms per frame", rs => A(rs, r => r.Grab?.MsPerFrame, "0.00"));
    Row("Read into one reused Mat: frames/s", rs => A(rs, r => r.Reused?.FramesPerSecond, "0.0"));
    Row("Read into one reused Mat: ms per frame, mean", rs => A(rs, r => r.Reused?.MsPerFrame, "0.00"));
    Row("retrieve step per frame (ReadNext - grab), ms", rs => A(rs, r => r.Decode is { } d && r.Grab is { } g ? d.MsPerFrame - g.MsPerFrame : null, "0.00"));
    Row("  of which colour conversion (reused Mat - grab), ms", rs => A(rs, r => r.Reused is { } u && r.Grab is { } g ? u.MsPerFrame - g.MsPerFrame : null, "0.00"));
    Row("  of which a new frame buffer (ReadNext - reused Mat), ms", rs => A(rs, r => r.Decode is { } d && r.Reused is { } u ? d.MsPerFrame - u.MsPerFrame : null, "0.00"));
    Row("logical processors busy: ReadNext loop", rs => A(rs, r => r.Decode?.CpuCores, "0.0"));
    Row("logical processors busy: reused-Mat loop", rs => A(rs, r => r.Reused?.CpuCores, "0.0"));
    Row("logical processors busy: grab-only loop", rs => A(rs, r => r.Grab?.CpuCores, "0.0"));

    Table($"Playback (PlaybackSession, {PlaybackOptions.Default.DecodeAheadFrames}-frame decode-ahead buffer = app default; sink disposes frames, no WPF upload)", "Lateness = present time - due time; the clock drops a frame only when it is more than one frame duration late.");
    Row("frame duration (budget per frame)", rs => $"{1000 / rs[0].Fps:0.0} ms");
    Row("presented fps / video fps", rs => A(rs, r => r.Playback?.PresentedFps, "0.0", $" / {rs[0].Fps:0.##}"));
    Row("late drops / frames", rs => A(rs, r => r.Playback?.DroppedLate, "0", $" / {rs[0].FramesUsed}"));
    Row("presentation lateness p50, ms", rs => A(rs, r => r.Playback?.LatenessP50Ms, "0.0"));
    Row("presentation lateness p99, ms", rs => A(rs, r => r.Playback?.LatenessP99Ms, "0.0"));
    Row("presentation lateness max, ms", rs => A(rs, r => r.Playback?.LatenessMaxMs, "0.0"));
    Row("frames presented more than half a frame late", rs => A(rs, r => r.Playback?.PresentedOverHalfFrameLate, "0"));
    Row("wall time / clip time", rs => A(rs, r => r.Playback?.WallSeconds, "0.0", $" s / {rs[0].FramesUsed / rs[0].Fps:0.0} s"));
    Row("1-frame buffer (pass 1): late drops", rs => rs[0].PlaybackOneFrameBuffer is { } p ? $"{p.DroppedLate} / {rs[0].FramesUsed}" : null);
    Row("1-frame buffer (pass 1): presented fps", rs => rs[0].PlaybackOneFrameBuffer is { } p ? $"{p.PresentedFps:0.0}" : null);
    Row("1-frame buffer (pass 1): lateness p99 / max, ms", rs => rs[0].PlaybackOneFrameBuffer is { } p ? $"{p.LatenessP99Ms:0.0} / {p.LatenessMaxMs:0.0}" : null);
    Row("1-frame buffer (pass 1): presented > half a frame late", rs => rs[0].PlaybackOneFrameBuffer is { } p ? $"{p.PresentedOverHalfFrameLate}" : null);

    Table("Seek (hover preview exact frame) and storyboard", "Seek cost depends mostly on the keyframe interval (GOP): a seek decodes from the keyframe before the target.");
    Row("keyframe interval (GOP), frames", rs => rs[0].Keyframes is { MedianGopFrames: { } med } ? $"{med:0}" : rs[0].Keyframes is { Count: 1 } ? $">= {rs[0].FramesUsed}" : null);
    Row("seek + ReadNext (40 random, seed 42): median ms", rs => A(rs, r => r.Seek?.MedianMs, "0"));
    Row("seek + ReadNext: p90 ms", rs => A(rs, r => r.Seek?.P90Ms, "0"));
    Row("seek + ReadNext: max ms", rs => A(rs, r => r.Seek?.MaxMs, "0"));
    Row("storyboard tiles (Plan: 2 s spacing, max 240)", rs => rs[0].Storyboard is { } s ? $"{s.Tiles}" : null);
    Row("storyboard build time (one decoder), s", rs => A(rs, r => r.Storyboard?.BuildSeconds, "0.00"));
    Row("storyboard ms per tile", rs => A(rs, r => r.Storyboard is { Tiles: > 0 } s ? s.BuildSeconds * 1000 / s.Tiles : null, "0"));
    Row("storyboard tile bytes (total)", rs => rs[0].Storyboard is { } s ? $"{s.TileBytes / Units.Kib:0} KiB ({s.TileWidth}x{s.TileHeight} each)" : null);

    var aiStages = reports.SelectMany(r => r.Ai).Select(a => a.Stage).Distinct().ToArray();
    Table("AI scan on the GPU (DirectML), AnalysisJob + IouObjectTracker", $"Main runs analyze every frame (stress test); the app default is YOLO11n at 5 analyzed frames per second. Real time = video seconds processed per wall second. Live-frame bound {aiBound}. Counts are from pass 1.");
    foreach (var stage in aiStages)
    {
        AiResult? Ai(VideoReport r) => r.Ai.FirstOrDefault(a => a.Stage == stage);
        AiResult? First(List<VideoReport> rs) => rs.Select(Ai).FirstOrDefault(a => a is not null);
        Row($"{stage}: sampling", rs => First(rs) is { } a ? a.Sampling + (a.Reduction is null ? "" : " (REDUCED)") : null);
        Row($"{stage}: frames analyzed", rs => First(rs) is { } a ? $"{a.FramesAnalyzed:N0}" : null);
        Row($"{stage}: analyzed frames/s (end to end)", rs => A(rs, r => Ai(r)?.AnalyzedPerSecond, "0.0"));
        Row($"{stage}: speed vs real time", rs => A(rs, r => Ai(r)?.RealTimeFactor, "0.00", "x"));
        Row($"{stage}: mean Detect ms", rs => A(rs, r => Ai(r)?.MeanDetectMs, "0.0"));
        Row($"{stage}: faces total (pass 1)", rs => First(rs) is { } a ? $"{a.Detections:N0}" : null);
        Row($"{stage}: faces per analyzed frame, mean / max (pass 1)", rs => First(rs) is { } a ? $"{a.MeanFaces:0.0} / {a.MaxFaces}" : null);
        Row($"{stage}: tracks", rs => A(rs, r => Ai(r)?.Tracks, "0"));
        Row($"{stage}: queue max / live frames peak (exact)", rs => First(rs) is { } a ? $"{rs.Select(Ai).OfType<AiResult>().Max(x => x.MaxQueue)}/4 / {rs.Select(Ai).OfType<AiResult>().Max(x => x.MaxLiveFrames)}" : null);
        Row($"{stage}: GPU busy, this process / others (%)", rs => A(rs, r => S(r, stage)?.GpuAppPercent, "0") is { } app ? $"{app} / {A(rs, r => S(r, stage)?.GpuOthersPercent, "0")}" : null);
        Row($"{stage}: dedicated VRAM, this process: peak (MiB)", rs => A(rs, r => S(r, stage)?.VramPeakMib, "0"));
        Row($"{stage}: dedicated VRAM increase over the stage start (MiB)", rs => A(rs, r => S(r, stage) is { VramPeakMib: { } p, VramBeforeMib: { } b } ? p - b : null, "0"));
    }

    Table("Export (YOLO11n every-frame detections blurred; FFmpeg writer as the app wires it)", "Total = ExportResult.Elapsed (includes ffprobe, ffmpeg start, encoder flush, +faststart). Steady = frames handed to the encoder between 10 % and 90 % of the export. App = this process (decode, blur, pipe); encoder = the ffmpeg.exe child (BGR-to-NV12 conversion and H.264 encoding).");
    Row("encoder", rs => rs[0].Export?.Encoder);
    Row("export frames/s, total", rs => A(rs, r => r.Export?.FramesPerSecond, "0.0"));
    Row("export frames/s, steady (10-90 %)", rs => A(rs, r => r.Export?.SteadyFramesPerSecond, "0.0"));
    Row("export ms per frame, steady", rs => A(rs, r => r.Export?.SteadyFramesPerSecond is { } f ? 1000 / f : null, "0.0"));
    Row("export speed vs real time (steady fps / video fps)", rs => A(rs, r => r.Export?.SteadyFramesPerSecond is { } f ? f / r.Fps : null, "0.00", "x"));
    Row("frames written (with blur)", rs => rs[0].Export is { } e ? $"{e.Frames:N0} ({e.FramesRedacted:N0})" : null);
    Row("output size", rs => A(rs, r => r.Export?.OutputMib, "0.0", " MiB"));
    Row("output bitrate", rs => A(rs, r => r.Export is { } e ? e.OutputMib * Units.Mib * 8 / (e.Frames / r.Fps) / 1e6 : null, "0.0", " Mbit/s"));
    Row("logical processors busy: app process", rs => A(rs, r => S(r, "export")?.AppCores, "0.0"));
    Row("logical processors busy: encoder process", rs => A(rs, r => S(r, "export")?.ChildCores, "0.0"));
    Row("logical processors busy: app + encoder", rs => A(rs, r => S(r, "export") is { } s ? s.AppCores + s.ChildCores : null, "0.0"));
    Row("encoder process peak private bytes (MiB)", rs => A(rs, r => S(r, "export")?.ChildPeakPrivateMib, "0"));
    Row("app process private increase (peak - before, MiB)", rs => A(rs, r => S(r, "export")?.IncreaseMib, "0"));

    var stages = reports.SelectMany(r => r.Stages).Select(s => s.Stage).Distinct().ToArray();
    Table("Memory per stage: private bytes increase (peak - settled level before the stage), MiB", "Raw peaks include whatever earlier stages left behind; the increase is the stage's own cost. App process only (the export encoder is in the export table).");
    foreach (var stage in stages)
    {
        Row(stage, rs => A(rs, r => S(r, stage)?.IncreaseMib, "0"));
    }

    Table("Memory per stage: raw peak private bytes, MiB (app process)");
    foreach (var stage in stages)
    {
        Row(stage, rs => A(rs, r => S(r, stage)?.PeakPrivateMib, "0"));
    }

    Table("Live native frames per stage: exact peak (all passes) / design bound", $"Bounds: decode and seek 1; playback buffer + 2 = {playbackBound} (1-frame buffer: 3); AI channel + 2 = {aiBound}; export 2 x 4 + 3 = {exportBound}.");
    foreach (var stage in stages)
    {
        Row(stage, rs => rs.Select(r => S(r, stage)).OfType<StageMemory>().ToArray() is { Length: > 0 } ss ? $"{ss.Max(s => s.PeakLiveFrames)} / {ss[0].LiveFrameBound}" : null);
    }

    Table("Private bytes after a settled GC: before -> after each stage (pass 1), MiB");
    foreach (var stage in stages)
    {
        Row(stage, rs => S(rs[0], stage) is { } s ? $"{s.PrivateBeforeMib:0} -> {s.PrivateAfterMib:0}" : null);
    }

    Row("whole video, start -> end (every pass)", rs => string.Join("; ", rs.Select(r => $"p{r.Pass}: {r.PrivateStartMib:0} -> {r.PrivateEndMib:0}")));

    Table("Stage time (s) and logical processors busy: app / child processes / rest of the system (medians across passes)");
    foreach (var stage in stages)
    {
        double Med(List<VideoReport> rs, Func<StageMemory, double> f)
        {
            var v = rs.Select(r => S(r, stage)).OfType<StageMemory>().Select(f).Order().ToArray();
            return v.Length == 0 ? double.NaN : v.Length % 2 == 1 ? v[v.Length / 2] : (v[(v.Length / 2) - 1] + v[v.Length / 2]) / 2;
        }

        Row(stage, rs => rs.Any(r => S(r, stage) is not null) ? $"{Med(rs, s => s.Seconds):0.0} s; {Med(rs, s => s.AppCores):0.0} / {Med(rs, s => s.ChildCores):0.0} / {Med(rs, s => s.OtherCores):0.0}" : null);
    }

    Table("Background: logical processors busy in other processes during each stage, highest across passes (at stage start)", "Context for the ranges: a pass with a high value here ran while something else on the machine was busy.");
    foreach (var stage in stages)
    {
        Row(stage, rs => rs.Select(r => S(r, stage)).OfType<StageMemory>().ToArray() is { Length: > 0 } ss ? $"{ss.Max(s => s.OtherCores):0.0} ({ss.Max(s => s.BackgroundAtStart):0.0})" : null);
    }

    Table("GPU per stage: engine busy this process / other processes (%), dedicated VRAM peak this process (MiB) (medians across passes)");
    foreach (var stage in stages)
    {
        Row(stage, rs => A(rs, r => S(r, stage)?.GpuAppPercent, "0") is null ? null : $"{Agg(rs.Select(r => S(r, stage)?.GpuAppPercent), "0")} / {Agg(rs.Select(r => S(r, stage)?.GpuOthersPercent), "0")}; VRAM {Agg(rs.Select(r => S(r, stage)?.VramPeakMib), "0")}");
    }

    var reduced = reports.SelectMany(r => r.Ai.Where(a => a.Reduction is not null).Select(a => $"{r.File} pass {r.Pass} {a.Stage}: {a.Reduction}")).ToArray();
    Console.WriteLine();
    Console.WriteLine(reduced.Length == 0 ? "Reduced stages: none." : "Reduced stages:\n- " + string.Join("\n- ", reduced));
    Console.WriteLine($"Frame limit: first {frameLimit:N0} frames of every video in every stage.");
}

static void Settle()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

// Private bytes (commit charge) of this process: PROCESS_MEMORY_COUNTERS_EX.PrivateUsage, the same counter the
// stage sampler reads, so "before", "peak" and "after" are directly comparable.
static unsafe long SelfPrivateBytes()
{
    var counters = new ProcessMemoryCountersEx { Size = (uint)sizeof(ProcessMemoryCountersEx) };
    return Win32.K32GetProcessMemoryInfo(Win32.GetCurrentProcess(), &counters, counters.Size) != 0 ? (long)counters.PrivateUsage : 0;
}

static TimeSpan SelfCpu()
{
    using var self = Process.GetCurrentProcess();
    return self.TotalProcessorTime;
}

int IntOption(string prefix, int fallback) =>
    args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal)) is { } option && int.TryParse(option.AsSpan(prefix.Length), out var value) && value > 0 ? value : fallback;

static string ShortName(DetectionModel model) => model.Id switch
{
    OnnxModelCatalog.UltraFaceId => "UltraFace",
    OnnxModelCatalog.YoloFaceId => "YOLO11n",
    OnnxModelCatalog.Yolo26FaceId => "YOLO26l",
    _ => model.DisplayName,
};

// Repository root = the folder holding VisionRedact.slnx, searched upwards from the working directory, then the exe.
static string FindRepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VisionRedact.slnx")))
            {
                return dir.FullName;
            }
        }
    }

    return Directory.GetCurrentDirectory();
}

static string? ResolveVideo(string input, string repoRoot) =>
    new[] { Path.GetFullPath(input), Path.GetFullPath(Path.Combine(repoRoot, input)) }.FirstOrDefault(File.Exists);

// Free physical memory (MEMORYSTATUSEX.ullAvailPhys): what the memory guard compares with 8 GiB.
static unsafe long AvailablePhysicalBytes()
{
    var status = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
    return Win32.GlobalMemoryStatusEx(&status) != 0 ? (long)status.AvailablePhysical : long.MaxValue;
}

// Which H.264 encoder the app's FFmpeg writer selected, and for h264_mf which Media Foundation transform FFmpeg loads
// (its verbose log names it; hardware MFTs carry MFT_ENUM_HARDWARE_URL_Attribute). Also why NVENC is not used.
static EncoderInfo DescribeEncoder(FfmpegVideoWriterFactory writers)
{
    var selected = writers.SelectedEncoder;
    if (selected is null)
    {
        return new EncoderInfo(null, null, null, null, null);
    }

    string[] test = ["-hide_banner", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30", "-frames:v", "10"];
    string? name = null, clsid = null;
    bool? hardware = null;
    if (selected == "h264_mf")
    {
        var (_, _, log) = RunTool(writers.FfmpegPath, ["-loglevel", "verbose", .. test, "-c:v", "h264_mf", "-pix_fmt", "nv12", "-f", "null", "-"], TimeSpan.FromSeconds(30));
        name = Regex.Match(log, @"MFT name: '([^']*)'").Groups[1].Value is { Length: > 0 } n ? n : null;
        clsid = Regex.Match(log, @"MFT_TRANSFORM_CLSID_Attribute=(\{[0-9a-fA-F-]+\})").Groups[1].Value is { Length: > 0 } c ? c : null;
        hardware = log.Contains("MFT_ENUM_HARDWARE_URL_Attribute", StringComparison.Ordinal);
    }

    string? nvenc = null;
    if (selected != "h264_nvenc")
    {
        var (exit, _, error) = RunTool(writers.FfmpegPath, ["-loglevel", "error", .. test, "-c:v", "h264_nvenc", "-preset", "p4", "-pix_fmt", "nv12", "-f", "null", "-"], TimeSpan.FromSeconds(30));
        nvenc = exit == 0
            ? "works"
            : string.Join(" ", error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2).Select(l => Regex.Replace(l, @"^(\[[^\]]*\]\s*)+", "")));
    }

    return new EncoderInfo(selected, name, clsid, hardware, nvenc);
}

// Keyframes within the frames used, from packet flags (ffprobe reads packets only, no decoding).
static KeyframeInfo ProbeKeyframes(string ffprobe, string path, int frameLimit)
{
    if (!File.Exists(ffprobe))
    {
        return new KeyframeInfo(0, null, null, [], "ffprobe not found");
    }

    var (exit, output, error) = RunTool(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=avg_frame_rate", "-of", "csv=p=0", path], TimeSpan.FromSeconds(30));
    var rate = output.Trim().Split('/') is [var num, var den] && double.TryParse(num, CultureInfo.InvariantCulture, out var a) && double.TryParse(den, CultureInfo.InvariantCulture, out var b) && b > 0 ? a / b : 0;
    if (exit != 0 || rate <= 0)
    {
        return new KeyframeInfo(0, null, null, [], "ffprobe: " + error.Trim());
    }

    var seconds = frameLimit / rate;
    (exit, output, error) = RunTool(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", "-read_intervals", $"%+{(seconds + 1).ToString("0.###", CultureInfo.InvariantCulture)}", path], TimeSpan.FromSeconds(60));
    if (exit != 0)
    {
        return new KeyframeInfo(0, null, null, [], "ffprobe: " + error.Trim());
    }

    var times = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split(','))
        .Where(p => p.Length >= 2 && p[1].Contains('K', StringComparison.Ordinal) && double.TryParse(p[0], CultureInfo.InvariantCulture, out _))
        .Select(p => double.Parse(p[0], CultureInfo.InvariantCulture))
        .Where(t => t < seconds - (0.5 / rate))
        .Order()
        .ToArray();
    var gops = times.Zip(times.Skip(1), (x, y) => Math.Round((y - x) * rate)).Order().ToArray();
    return new KeyframeInfo(times.Length, gops.Length == 0 ? null : gops[gops.Length / 2], gops.Length == 0 ? null : gops[^1], [.. times.Take(12).Select(t => Math.Round(t, 3))], null);
}

// Runs a helper program to completion: exit code, stdout, stderr (both pipes read concurrently).
static (int ExitCode, string Output, string Error) RunTool(string exe, IEnumerable<string> arguments, TimeSpan timeout)
{
    using var process = new Process { StartInfo = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
    foreach (var argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }

    try
    {
        process.Start();
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
        return (-1, "", ex.Message);
    }

    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(timeout))
    {
        process.Kill(entireProcessTree: true);
        return (-1, "", "timed out");
    }

    process.WaitForExit();
    return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
}

// ---------------------------------------------------------------------------------------------
// B9. Scene finder: both face models over a whole video (app default thresholds), their agreement, and the scenes
// where both detect well vs where only YOLO finds the faces. No ground truth: this compares the models, it does not
// measure accuracy against human labels.
// ---------------------------------------------------------------------------------------------
void RunSceneFinder(string path, int sampleEvery, int windowSeconds)
{
    var target = DxgiAdapterCatalog.Preferred(targets);
    var factory = new OnnxDetectorFactory(options);
    using var yolo = factory.Create(models.First(m => m.Id == OnnxModelCatalog.YoloFaceId), target);
    using var ultra = factory.Create(models.First(m => m.Id == OnnxModelCatalog.UltraFaceId), target);

    var samples = new List<(long Frame, DetectionCandidate[] Yolo, DetectionCandidate[] Ultra)>();
    double yoloMs = 0, ultraMs = 0;
    int width, height;
    double fps;
    using (var decoder = decoders.Open(path))
    {
        (width, height, fps) = (decoder.Metadata.Width, decoder.Metadata.Height, decoder.Metadata.FramesPerSecond);
        for (long f = 0; ; f++)
        {
            if (f % sampleEvery != 0)
            {
                if (!decoder.Skip())
                {
                    break;
                }

                continue;
            }

            using var frame = decoder.ReadNext();
            if (frame is null)
            {
                break;
            }

            var sw = Stopwatch.StartNew();
            var y = yolo.Detect(frame).Where(c => c.Class == ObjectClass.Face).ToArray();
            yoloMs += sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var u = ultra.Detect(frame).Where(c => c.Class == ObjectClass.Face).ToArray();
            ultraMs += sw.Elapsed.TotalMilliseconds;
            samples.Add((f, y, u));
        }
    }

    // Greedy one-to-one matching of the two models' boxes (IoU ≥ 0.4).
    static int Matches(DetectionCandidate[] a, DetectionCandidate[] b)
    {
        var used = new bool[b.Length];
        var matched = 0;
        foreach (var x in a)
        {
            var best = -1;
            var bestIou = 0.4;
            for (var j = 0; j < b.Length; j++)
            {
                var iou = x.Box.IntersectionOverUnion(b[j].Box);
                if (!used[j] && iou >= bestIou)
                {
                    (best, bestIou) = (j, iou);
                }
            }

            if (best >= 0)
            {
                used[best] = true;
                matched++;
            }
        }

        return matched;
    }

    string Size(DetectionCandidate c) => (c.Box.Height * height) switch { < 32 => "small (<32 px)", < 96 => "medium (32–96 px)", _ => "large (≥96 px)" };
    string Time(long frame) => TimeSpan.FromSeconds(frame / fps).ToString(@"mm\:ss");

    var n = samples.Count;
    Console.WriteLine($"## B9 — Scene finder: {Path.GetFileName(path)}, {width}x{height} @ {fps:0.###} fps, {n:N0} sampled frames (1 in {sampleEvery}), app default thresholds, {yolo.Info.ExecutionProvider}");
    Console.WriteLine("| model | frames with ≥1 face | faces | small | medium | large | ms/frame |");
    foreach (var (name, pick, ms) in new[] { ("YOLO11n-face", 0, yoloMs), ("UltraFace RFB-320", 1, ultraMs) })
    {
        var all = samples.SelectMany(s => pick == 0 ? s.Yolo : s.Ultra).ToArray();
        var bySize = all.GroupBy(Size).ToDictionary(g => g.Key, g => g.Count());
        var withFace = samples.Count(s => (pick == 0 ? s.Yolo : s.Ultra).Length > 0);
        Console.WriteLine($"| {name} | {withFace:N0} ({100.0 * withFace / n:0.0} %) | {all.Length:N0} | {bySize.GetValueOrDefault("small (<32 px)"):N0} | {bySize.GetValueOrDefault("medium (32–96 px)"):N0} | {bySize.GetValueOrDefault("large (≥96 px)"):N0} | {ms / n:0.0} |");
    }

    var matchedTotal = samples.Sum(s => Matches(s.Yolo, s.Ultra));
    var yoloTotal = samples.Sum(s => s.Yolo.Length);
    var ultraTotal = samples.Sum(s => s.Ultra.Length);
    Console.WriteLine($"Agreement: {matchedTotal:N0} faces found by both · {yoloTotal - matchedTotal:N0} only by YOLO · {ultraTotal - matchedTotal:N0} only by UltraFace");

    var windowFrames = (long)Math.Round(windowSeconds * fps);
    var windows = samples.GroupBy(s => s.Frame / windowFrames).Select(g =>
    {
        var list = g.ToList();
        var bothGood = list.Count(s => s.Yolo.Length > 0 && Matches(s.Yolo, s.Ultra) >= 0.8 * Math.Max(s.Yolo.Length, s.Ultra.Length));
        var yoloFaces = list.Sum(s => s.Yolo.Length);
        var ultraFaces = list.Sum(s => s.Ultra.Length);
        var faceHeights = list.SelectMany(s => s.Yolo).Select(c => c.Box.Height * height).OrderBy(h => h).ToArray();
        return (Start: g.Key * windowFrames, Samples: list.Count, BothGood: (double)bothGood / list.Count,
            YoloSeen: (double)list.Count(s => s.Yolo.Length > 0) / list.Count, YoloFaces: yoloFaces, UltraFaces: ultraFaces,
            MedianHeight: faceHeights.Length == 0 ? 0 : faceHeights[faceHeights.Length / 2]);
    }).ToArray();

    Console.WriteLine();
    Console.WriteLine($"### Scenes where both models agree ({windowSeconds} s windows; ≥ 80 % of sampled frames with a face found by both)");
    Console.WriteLine("| scene | frames where both agree | faces (YOLO / UltraFace) | median face height |");
    foreach (var w in windows.Where(w => w.BothGood >= 0.8).OrderByDescending(w => w.BothGood).ThenByDescending(w => w.YoloFaces).Take(8).OrderBy(w => w.Start))
    {
        Console.WriteLine($"| {Time(w.Start)}–{Time(w.Start + windowFrames)} | {100 * w.BothGood:0} % | {w.YoloFaces} / {w.UltraFaces} | {w.MedianHeight:0} px |");
    }

    Console.WriteLine();
    Console.WriteLine($"### Scenes where only YOLO finds the faces (YOLO sees a face in ≥ 60 % of frames, UltraFace finds ≤ 25 % as many)");
    Console.WriteLine("| scene | frames with a YOLO face | faces (YOLO / UltraFace) | median face height |");
    foreach (var w in windows.Where(w => w.YoloSeen >= 0.6 && w.UltraFaces <= 0.25 * w.YoloFaces).OrderByDescending(w => w.YoloFaces).Take(8).OrderBy(w => w.Start))
    {
        Console.WriteLine($"| {Time(w.Start)}–{Time(w.Start + windowFrames)} | {100 * w.YoloSeen:0} % | {w.YoloFaces} / {w.UltraFaces} | {w.MedianHeight:0} px |");
    }
}

// ---------------------------------------------------------------------------------------------
// B8. Box accuracy between analyzed frames: hold vs interpolation, per tracker. Ground truth = YOLO on every frame.
// ---------------------------------------------------------------------------------------------
void RunTrackingAccuracy(string path, int frameLimit, int sampleEvery)
{
    Directory.Delete(work, recursive: true);
    var model = models.First(m => m.Id == OnnxModelCatalog.YoloFaceId);
    var target = DxgiAdapterCatalog.Preferred(targets);
    using var detector = new OnnxDetectorFactory(options).Create(model, target);

    var truth = new List<IReadOnlyList<Detection>>();
    int width, height;
    long frameCount;
    using (var decoder = decoders.Open(path))
    {
        (width, height, frameCount) = (decoder.Metadata.Width, decoder.Metadata.Height, decoder.Metadata.FrameCount);
        for (var f = 0; f < frameLimit; f++)
        {
            using var frame = decoder.ReadNext();
            if (frame is null)
            {
                break;
            }

            var index = f;
            truth.Add(detector.Detect(frame).Select(c => Detection.Create(index, c.Box, c.Confidence, c.Class)).ToArray());
        }
    }

    var faces = truth.Sum(t => t.Count);
    Console.WriteLine($"## B8 — Box accuracy between samples: {Path.GetFileName(path)}, {truth.Count} frames, 1 of {sampleEvery} analyzed, ground truth = {model.DisplayName} on every frame ({faces:N0} faces) on {detector.Info.ExecutionProvider}");
    Console.WriteLine("| method | tracks | faces between samples | covered by box (+20 % padding) | centre error p50 | p95 | visible drift > 8 px (box within 100 px) |");
    Evaluate("hold (no interpolation)", () => null);
    Evaluate("interpolate, IoU + prediction", () => new VisionRedact.Application.Tracking.IouObjectTracker(maxCentreDistance: 0));
    Evaluate("interpolate, IoU + prediction + distance", () => new VisionRedact.Application.Tracking.IouObjectTracker());

    void Evaluate(string label, Func<VisionRedact.Application.Tracking.IObjectTracker?> trackerFactory)
    {
        var repo = new InMemoryDetectionRepository();
        repo.Reset(frameCount);
        repo.HoldFrames = sampleEvery - 1;
        var tracker = trackerFactory();
        var trackIds = new HashSet<int>();
        for (var f = 0; f < truth.Count; f += sampleEvery)
        {
            var detections = tracker?.Assign(f, truth[f]) ?? truth[f];
            foreach (var d in detections)
            {
                if (d.TrackId is { } id)
                {
                    trackIds.Add(id);
                }
            }

            repo.AddFrameResults(f, detections);
        }

        var errors = new List<double>();
        int total = 0, covered = 0;
        for (var f = 0; f < truth.Count; f++)
        {
            if (f % sampleEvery == 0)
            {
                continue;
            }

            var shown = repo.GetForDisplay(f);
            foreach (var face in truth[f])
            {
                total++;
                var centre = face.Box.Center;
                if (shown.Any(s => s.Box.Inflate(0.2).Contains(centre)))
                {
                    covered++;
                }

                if (shown.Count > 0)
                {
                    errors.Add(shown.Min(s => Math.Sqrt(Math.Pow((s.Box.Center.X - centre.X) * width, 2) + Math.Pow((s.Box.Center.Y - centre.Y) * height, 2))));
                }
            }
        }

        errors.Sort();
        var near = errors.Where(e => e <= 100).ToArray();
        var drift = near.Length == 0 ? 0 : 100.0 * near.Count(e => e > 8) / near.Length;
        var tracks = tracker is null ? "—" : trackIds.Count.ToString("N0");
        Console.WriteLine($"| {label,-42} | {tracks,6} | {total,7:N0} | {100.0 * covered / Math.Max(1, total),6:0.0} % | {Percentile(errors, 0.5),6:0.0} px | {Percentile(errors, 0.95),6:0.0} px | {drift,5:0.0} % |");
    }
}

IEnumerable<(string Label, string Path, int Frames)> Sources()
{
    yield return ("synthetic 1080p MJPG", synthetic, 900);
    if (realVideo is not null)
    {
        yield return ("real " + Path.GetExtension(realVideo).TrimStart('.').ToUpperInvariant(), realVideo, 1800);
    }
}

(double Fps, double AllocatedMb, long FrameAllocs) Scan(string path, int frames, int sampleEvery, bool useSkip)
{
    using var decoder = decoders.Open(path);
    var allocs = 0L;
    var before = GC.GetTotalAllocatedBytes(precise: true);
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < frames; i++)
    {
        if (i % sampleEvery != 0 && useSkip)
        {
            if (!decoder.Skip())
            {
                break;
            }

            continue;
        }

        using var frame = decoder.ReadNext();
        if (frame is null)
        {
            break;
        }

        allocs++;
    }

    return (frames / sw.Elapsed.TotalSeconds, (GC.GetTotalAllocatedBytes(precise: true) - before) / 1_048_576.0, allocs);
}

(double MsPerFrame, double AllocatedKbPerFrame, int Gen0, int Gen1, int Gen2) MeasurePreprocess(OwnedVideoFrame frame, bool reuse, int iterations)
{
    var spec = new PreprocessSpec(640, 640, 0f, 1f / 255f, 114);
    using var shared = new LetterboxPreprocessor(spec);
    GC.Collect();
    var (g0, g1, g2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    var before = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < iterations; i++)
    {
        if (reuse)
        {
            shared.Prepare(frame);
        }
        else
        {
            // Baseline: what a straightforward implementation does — new buffers for every frame.
            using var perFrame = new LetterboxPreprocessor(spec);
            perFrame.Prepare(frame);
        }
    }

    var ms = sw.Elapsed.TotalMilliseconds / iterations;
    var kb = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations / 1024;
    return (ms, kb, GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2);
}

static double Percentile(List<double> sorted, double p) => sorted[(int)Math.Clamp(Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

static bool IsRelease()
{
#if DEBUG
    return false;
#else
    return true;
#endif
}

OwnedVideoFrame FirstFrame(string path)
{
    using var decoder = decoders.Open(path);
    decoder.Seek(Math.Min(14_000, decoder.Metadata.LastFrameIndex));
    return decoder.ReadNext() ?? throw new InvalidOperationException("No frame.");
}

static MatVideoFrame MakeFrame(int width, int height)
{
    var mat = new Mat(height, width, DepthType.Cv8U, 3);
    CvInvoke.Randu(mat, new MCvScalar(0, 0, 0), new MCvScalar(255, 255, 255));
    return new MatVideoFrame(mat, 0, TimeSpan.Zero);
}

static void WriteSyntheticVideo(string path, int width, int height, double fps, int frames)
{
    using var writer = new VideoWriter(path, VideoWriter.Fourcc('M', 'J', 'P', 'G'), fps, new Size(width, height), true);
    using var mat = new Mat(height, width, DepthType.Cv8U, 3);
    for (var i = 0; i < frames; i++)
    {
        mat.SetTo(new MCvScalar(30 + (i % 50), 40, 50));
        CvInvoke.Circle(mat, new Point((i * 7) % width, height / 2), 120, new MCvScalar(200, 180, 160), -1);
        CvInvoke.PutText(mat, $"frame {i}", new Point(60, 120), FontFace.HersheySimplex, 3, new MCvScalar(255, 255, 255), 5);
        writer.Write(mat);
    }
}

static VisionRedact.Domain.Redaction.RedactionRegion RedactionRegion(long start, long end) =>
    VisionRedact.Domain.Redaction.RedactionRegion.Create(new NormalizedRect(0.3, 0.3, 0.3, 0.3), start, end, VisionRedact.Domain.Redaction.RedactionEffectKind.GaussianBlur);

internal sealed class DisposingSink : IPreviewFrameSink
{
    public void Present(OwnedVideoFrame frame) => frame.Dispose();

    public void Clear()
    {
    }
}

/// <summary>
/// Stops the stream after N frames (bounded benchmark run on a longer file) and reports N as the frame count, so
/// anything planned from the metadata (storyboard, analysis totals, export progress) covers exactly those frames.
/// </summary>
internal sealed class LimitedDecoderFactory(IVideoDecoderFactory inner, long limit) : IVideoDecoderFactory
{
    public IVideoDecoder Open(string path) => new Limited(inner.Open(path), limit);

    private sealed class Limited : IVideoDecoder
    {
        private readonly IVideoDecoder _inner;
        private readonly long _limit;

        public Limited(IVideoDecoder inner, long limit)
        {
            _inner = inner;
            _limit = limit;
            var m = inner.Metadata;
            Metadata = m.FrameCount <= limit ? m : new VisionRedact.Domain.Media.VideoMetadata(m.SourcePath, m.Width, m.Height, m.FramesPerSecond, limit, m.FileSizeBytes, m.Codec);
        }

        public VisionRedact.Domain.Media.VideoMetadata Metadata { get; }

        public string BackendName => _inner.BackendName;

        public long Position => _inner.Position;

        public void Seek(long frameIndex) => _inner.Seek(Math.Min(frameIndex, _limit - 1));

        public OwnedVideoFrame? ReadNext() => _inner.Position >= _limit ? null : _inner.ReadNext();

        public bool Skip() => _inner.Position < _limit && _inner.Skip();

        public void Dispose() => _inner.Dispose();
    }
}

// ---------------------------------------------------------------------------------------------
// B10 result rows (also serialized to docs/blog/_research/b10-results.json). Memory in MiB (1,048,576 bytes).
// ---------------------------------------------------------------------------------------------
internal static class Units
{
    public const double Kib = 1024;
    public const double Mib = 1024 * 1024;
    public const double Gib = 1024 * 1024 * 1024;
}

internal sealed class VideoReport
{
    public string File { get; set; } = "";

    /// <summary>Measured pass (1..N); 0 = the unreported warm-up pass.</summary>
    public int Pass { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public double Fps { get; set; }

    public long ContainerFrames { get; set; }

    public long FramesUsed { get; set; }

    public double DurationSeconds { get; set; }

    public long FileBytes { get; set; }

    public string Codec { get; set; } = "";

    public string Backend { get; set; } = "";

    public long BytesPerFrame { get; set; }

    public KeyframeInfo? Keyframes { get; set; }

    public DecodeResult? Decode { get; set; }

    public GrabResult? Grab { get; set; }

    public ReusedMatResult? Reused { get; set; }

    public SeekResult? Seek { get; set; }

    public StoryboardResult? Storyboard { get; set; }

    public PlaybackResult? Playback { get; set; }

    public PlaybackResult? PlaybackOneFrameBuffer { get; set; }

    public List<AiResult> Ai { get; } = [];

    public ExportRow? Export { get; set; }

    public List<StageMemory> Stages { get; } = [];

    public double PrivateStartMib { get; set; }

    public double PrivateEndMib { get; set; }
}

internal sealed record KeyframeInfo(int Count, double? MedianGopFrames, double? MaxGopFrames, double[] FirstKeyframeSeconds, string? Error);

internal sealed record DecodeResult(int FramesTimed, double FramesPerSecond, double MsPerFrame, double P50Ms, double P99Ms, double MaxMs, double Headroom, double CpuCores);

internal sealed record GrabResult(int Frames, double FramesPerSecond, double MsPerFrame, double CpuCores);

internal sealed record ReusedMatResult(int Frames, double FramesPerSecond, double MsPerFrame, double P99Ms, double CpuCores);

internal sealed record SeekResult(int Seeks, int Seed, double MedianMs, double P90Ms, double MaxMs, double MeanMs);

internal sealed record StoryboardResult(int Tiles, int TilesPlanned, double BuildSeconds, long TileBytes, int TileWidth, int TileHeight, int PeakFramesHeld);

internal sealed record PlaybackResult(
    int DecodeAheadFrames,
    double ClipSeconds,
    double WallSeconds,
    long Decoded,
    long Presented,
    long DroppedLate,
    double PresentedFps,
    double AverageDecodeMs,
    string FinalState,
    int LatenessSamples,
    double LatenessP50Ms,
    double LatenessP99Ms,
    double LatenessMaxMs,
    int PresentedOverHalfFrameLate);

internal sealed record AiResult(
    string Stage,
    string Model,
    string Provider,
    int InputSize,
    string Sampling,
    string? Reduction,
    long FramesAnalyzed,
    double Seconds,
    double AnalyzedPerSecond,
    double RealTimeFactor,
    double MeanDetectMs,
    long Detections,
    double MeanFaces,
    int MaxFaces,
    int Tracks,
    int MaxQueue,
    long MaxLiveFrames,
    double LoadSeconds,
    double WarmDetectMs);

internal sealed record ExportRow(string Encoder, long Frames, long FramesRedacted, double Seconds, double FramesPerSecond, double? SteadyFramesPerSecond, double OutputMib, bool Audio);

internal sealed record EncoderInfo(string? Selected, string? MftName, string? MftClsid, bool? MftIsHardware, string? NvencProbe);

internal sealed record LeakRow(string Model, long Frames, double BeforeMib, double[] AfterMib);

internal sealed class StageMemory
{
    public string Stage { get; init; } = "";

    public double Seconds { get; init; }

    public double PrivateBeforeMib { get; init; }

    public double PrivateAfterMib { get; init; }

    public double PeakPrivateMib { get; init; }

    /// <summary>The stage's own cost: peak private bytes minus the settled level before the stage.</summary>
    public double IncreaseMib => PeakPrivateMib - PrivateBeforeMib;

    public double PeakWorkingSetMib { get; init; }

    public long PeakLiveFrames { get; init; }

    public long LiveFrameBound { get; init; }

    /// <summary>Logical processors busy on average: this process.</summary>
    public double AppCores { get; init; }

    /// <summary>Logical processors busy on average: child processes (ffmpeg.exe, ffprobe.exe).</summary>
    public double ChildCores { get; init; }

    public string ChildProcesses { get; init; } = "";

    /// <summary>Peak private commit of the child processes (sum of their PeakPagefileUsage).</summary>
    public double ChildPeakPrivateMib { get; init; }

    /// <summary>Logical processors busy on average: the whole system (GetSystemTimes).</summary>
    public double SystemCores { get; init; }

    /// <summary>Everything else on the machine: system minus this process minus its children.</summary>
    public double OtherCores => Math.Max(0, SystemCores - AppCores - ChildCores);

    public double? GpuAppPercent { get; init; }

    public double? GpuOthersPercent { get; init; }

    public double? VramBeforeMib { get; init; }

    public double? VramPeakMib { get; init; }

    public double SamplerMsPerSecond { get; init; }

    /// <summary>Logical processors busy in other processes just before the stage (0.5 s window).</summary>
    public double BackgroundAtStart { get; init; }

    public double QuietWaitSeconds { get; init; }

    public string? Note { get; init; }
}

internal sealed record StageSample(
    double Seconds,
    long PeakPrivate,
    long PeakWorkingSet,
    double AppCores,
    double ChildCores,
    string ChildNames,
    long ChildPeakCommit,
    double SystemCores,
    double? GpuAppPercent,
    double? GpuOthersPercent,
    long? VramFirst,
    long? VramPeak,
    double SamplerMsPerSecond);

/// <summary>
/// One stage's measurements. Every 100 ms: private bytes / working set of this process and of its known child
/// processes (GetProcessMemoryInfo, microseconds). Every 500 ms: new child processes (found by parent PID with a
/// Toolhelp snapshot, ~20 ms; a handle is kept so their CPU time can be read after they exit), GPU engine use and
/// dedicated VRAM (PDH). CPU of this process, of the children and of the whole system comes from process / system
/// times at start and stop, so it is exact. A child that starts and exits between two 500 ms discoveries (a short
/// ffprobe) is missed. The sampler's own time is reported (SamplerMsPerSecond) and is part of the app's CPU.
/// </summary>
internal sealed unsafe class ProcessSampler : IDisposable
{
    private const int TickMs = 100;
    private const int SlowEveryTicks = 5;
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly int _selfPid = Environment.ProcessId;
    private readonly long _selfCreated;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<int, ChildProcess> _children = [];
    private readonly GpuCounters? _gpu;
    private readonly long _started;
    private readonly TimeSpan _selfCpuStart;
    private readonly (long Idle, long Kernel, long User) _systemStart;
    private long _peakPrivate;
    private long _peakWorkingSet;
    private int _ticks;
    private bool _initial = true;
    private bool _stopped;
    private double _samplerMs;

    public ProcessSampler(GpuCounters? gpu)
    {
        _gpu = gpu;
        _selfCreated = ChildProcess.CreationTime(Win32.GetCurrentProcess());
        _gpu?.BeginStage();
        _selfCpuStart = _self.TotalProcessorTime;
        _systemStart = SystemCpu.Times();
        _started = Stopwatch.GetTimestamp();
        Sample(slow: true);
        _initial = false;
        _timer = new System.Threading.Timer(_ => Sample(slow: ++_ticks % SlowEveryTicks == 0), null, TickMs, TickMs);
    }

    /// <summary>Takes a last sample, stops sampling and returns the stage's numbers.</summary>
    public StageSample Stop()
    {
        Sample(slow: true);
        lock (_gate)
        {
            _stopped = true;
            _gpu?.Collect();
            var seconds = Stopwatch.GetElapsedTime(_started).TotalSeconds;
            _self.Refresh();
            var app = (_self.TotalProcessorTime - _selfCpuStart).TotalSeconds / seconds;
            var children = _children.Values.Sum(c => c.CpuSeconds()) / seconds;
            var system = SystemCpu.BusyProcessors(_systemStart, SystemCpu.Times(), seconds);
            var names = string.Join(", ", _children.Values.GroupBy(c => c.Name).Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} x {g.Key}"));
            var commit = _children.Values.Sum(c => c.PeakCommit);
            return new StageSample(seconds, _peakPrivate, _peakWorkingSet, app, children, names, commit, system,
                _gpu?.AppBusyPercent, _gpu?.OthersBusyPercent, _gpu?.DedicatedFirst, _gpu?.DedicatedPeak, _samplerMs / seconds);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true; // a timer callback already queued sees this and returns
        }

        _timer.Dispose();
        lock (_gate)
        {
            foreach (var child in _children.Values)
            {
                child.Dispose();
            }

            _self.Dispose();
        }
    }

    private void Sample(bool slow)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var counters = new ProcessMemoryCountersEx { Size = (uint)sizeof(ProcessMemoryCountersEx) };
            if (Win32.K32GetProcessMemoryInfo(Win32.GetCurrentProcess(), &counters, counters.Size) != 0)
            {
                _peakPrivate = Math.Max(_peakPrivate, (long)counters.PrivateUsage);
                _peakWorkingSet = Math.Max(_peakWorkingSet, (long)counters.WorkingSetSize);
            }

            if (slow)
            {
                foreach (var (pid, name) in Toolhelp.Children(_selfPid))
                {
                    if (!_children.ContainsKey(pid) && ChildProcess.TryOpen(pid, name, _selfCreated, baseline: _initial) is { } child)
                    {
                        _children[pid] = child;
                    }
                }

                if (!_initial)
                {
                    _gpu?.Collect();
                }
            }

            foreach (var child in _children.Values)
            {
                child.SampleMemory();
            }

            _samplerMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }
}

/// <summary>A child process seen during a stage: its handle keeps CPU times readable after it exits.</summary>
internal sealed unsafe class ChildProcess : IDisposable
{
    private readonly nint _handle;
    private readonly long _baselineTicks;

    private ChildProcess(int pid, string name, nint handle, long baselineTicks)
    {
        Pid = pid;
        Name = name;
        _handle = handle;
        _baselineTicks = baselineTicks;
    }

    public int Pid { get; }

    public string Name { get; }

    public long PeakPrivate { get; private set; }

    /// <summary>PeakPagefileUsage: the process's lifetime peak of private committed memory.</summary>
    public long PeakCommit { get; private set; }

    /// <summary>Opens a process found by parent PID; ignores a stale PID created before the parent itself.</summary>
    public static ChildProcess? TryOpen(int pid, string name, long parentCreated, bool baseline)
    {
        const uint queryLimitedInformation = 0x1000;
        var handle = Win32.OpenProcess(queryLimitedInformation, 0, (uint)pid);
        if (handle == 0)
        {
            return null;
        }

        long created, exited, kernel, user;
        if (Win32.GetProcessTimes(handle, &created, &exited, &kernel, &user) == 0 || created < parentCreated)
        {
            Win32.CloseHandle(handle);
            return null;
        }

        return new ChildProcess(pid, name, handle, baseline ? kernel + user : 0);
    }

    public static long CreationTime(nint process)
    {
        long created, exited, kernel, user;
        return Win32.GetProcessTimes(process, &created, &exited, &kernel, &user) != 0 ? created : 0;
    }

    public void SampleMemory()
    {
        var counters = new ProcessMemoryCountersEx { Size = (uint)sizeof(ProcessMemoryCountersEx) };
        if (Win32.K32GetProcessMemoryInfo(_handle, &counters, counters.Size) != 0)
        {
            PeakPrivate = Math.Max(PeakPrivate, (long)counters.PrivateUsage);
            PeakCommit = Math.Max(PeakCommit, (long)counters.PeakPagefileUsage);
        }
    }

    /// <summary>User + kernel CPU seconds since the baseline (works after the process exited: the handle is open).</summary>
    public double CpuSeconds()
    {
        SampleMemory();
        long created, exited, kernel, user;
        return Win32.GetProcessTimes(_handle, &created, &exited, &kernel, &user) != 0 ? (kernel + user - _baselineTicks) / 1e7 : 0;
    }

    public void Dispose() => Win32.CloseHandle(_handle);
}

/// <summary>Child processes by parent PID (CreateToolhelp32Snapshot).</summary>
internal static unsafe class Toolhelp
{
    public static List<(int Pid, string Name)> Children(int parent)
    {
        const uint snapProcess = 0x2;
        var result = new List<(int, string)>();
        var snapshot = Win32.CreateToolhelp32Snapshot(snapProcess, 0);
        if (snapshot == -1)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)sizeof(ProcessEntry32) };
            for (var ok = Win32.Process32FirstW(snapshot, &entry); ok != 0; ok = Win32.Process32NextW(snapshot, &entry))
            {
                if (entry.ParentProcessId == parent)
                {
                    result.Add(((int)entry.ProcessId, new string(entry.ExeFile)));
                }
            }
        }
        finally
        {
            Win32.CloseHandle(snapshot);
        }

        return result;
    }
}

/// <summary>Whole-system CPU from GetSystemTimes (kernel time includes idle time).</summary>
internal static unsafe class SystemCpu
{
    public static (long Idle, long Kernel, long User) Times()
    {
        long idle, kernel, user;
        return Win32.GetSystemTimes(&idle, &kernel, &user) != 0 ? (idle, kernel, user) : (0, 0, 0);
    }

    public static double BusyProcessors((long Idle, long Kernel, long User) start, (long Idle, long Kernel, long User) end, double seconds) =>
        ((end.Kernel - start.Kernel) - (end.Idle - start.Idle) + (end.User - start.User)) / 1e7 / seconds;
}

/// <summary>
/// GPU use per process from the Windows performance counters Task Manager uses: "GPU Engine(*)\Utilization
/// Percentage" (per process and engine) and "GPU Process Memory(*)\Dedicated Usage" (bytes of dedicated VRAM).
/// Busy % per sample = the busiest engine, as Task Manager shows it, for this process and for all other processes.
/// </summary>
internal sealed unsafe class GpuCounters : IDisposable
{
    private const uint FormatDouble = 0x00000200, FormatLarge = 0x00000400, FormatNoCap100 = 0x00008000;
    private const uint MoreData = 0x800007D2;
    private readonly nint _query;
    private readonly nint _engine;
    private readonly nint _memory;
    private readonly string _prefix;
    private int _collects;
    private double _appSum;
    private double _othersSum;
    private int _busySamples;

    private GpuCounters(nint query, nint engine, nint memory, int pid)
    {
        _query = query;
        _engine = engine;
        _memory = memory;
        _prefix = $"pid_{pid}_";
    }

    public double? AppBusyPercent => _busySamples == 0 ? null : _appSum / _busySamples;

    public double? OthersBusyPercent => _busySamples == 0 ? null : _othersSum / _busySamples;

    public long? DedicatedFirst { get; private set; }

    public long? DedicatedPeak { get; private set; }

    public static GpuCounters? TryCreate(int pid)
    {
        if (Pdh.PdhOpenQueryW(null, 0, out var query) != 0)
        {
            return null;
        }

        if (Pdh.PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", 0, out var engine) != 0
            || Pdh.PdhAddEnglishCounterW(query, @"\GPU Process Memory(*)\Dedicated Usage", 0, out var memory) != 0)
        {
            Pdh.PdhCloseQuery(query);
            return null;
        }

        return new GpuCounters(query, engine, memory, pid);
    }

    /// <summary>Resets the per-stage numbers and takes the first collection (rates need two).</summary>
    public void BeginStage()
    {
        (_collects, _appSum, _othersSum, _busySamples, DedicatedFirst, DedicatedPeak) = (0, 0, 0, 0, null, null);
        Collect();
    }

    public void Collect()
    {
        if (Pdh.PdhCollectQueryData(_query) != 0)
        {
            return;
        }

        _collects++;
        long dedicated = 0;
        foreach (var (name, value) in Read(_memory, FormatLarge))
        {
            if (name.StartsWith(_prefix, StringComparison.Ordinal))
            {
                dedicated += (long)value;
            }
        }

        DedicatedFirst ??= dedicated;
        DedicatedPeak = Math.Max(DedicatedPeak ?? 0, dedicated);
        if (_collects < 2)
        {
            return;
        }

        var app = new Dictionary<string, double>();
        var others = new Dictionary<string, double>();
        foreach (var (name, value) in Read(_engine, FormatDouble | FormatNoCap100))
        {
            var luid = name.IndexOf("_luid_", StringComparison.Ordinal);
            if (luid < 0)
            {
                continue;
            }

            var engineKey = name[(luid + 1)..];
            var target = name.StartsWith(_prefix, StringComparison.Ordinal) ? app : others;
            target[engineKey] = target.GetValueOrDefault(engineKey) + value;
        }

        _appSum += app.Count == 0 ? 0 : app.Values.Max();
        _othersSum += others.Count == 0 ? 0 : others.Values.Max();
        _busySamples++;
    }

    public void Dispose() => Pdh.PdhCloseQuery(_query);

    private static List<(string Name, double Value)> Read(nint counter, uint format)
    {
        var result = new List<(string, double)>();
        uint size = 0;
        if (Pdh.PdhGetFormattedCounterArrayW(counter, format, ref size, out _, 0) != MoreData || size == 0)
        {
            return result;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Pdh.PdhGetFormattedCounterArrayW(counter, format, ref size, out var count, buffer) != 0)
            {
                return result;
            }

            var items = (PdhCounterValueItem*)buffer;
            for (var i = 0; i < count; i++)
            {
                if (items[i].Status is 0 or 1) // PDH_CSTATUS_VALID_DATA, PDH_CSTATUS_NEW_DATA
                {
                    var value = (format & FormatLarge) != 0 ? items[i].LargeValue : items[i].DoubleValue;
                    result.Add((Marshal.PtrToStringUni(items[i].Name) ?? "", value));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }
}

/// <summary>
/// Records when each frame is presented (then disposes it, like DisposingSink). Lateness = present time minus the
/// frame's due time, from the first frame presented after Play (PlaybackClock's anchor) at 1 / fps per frame.
/// </summary>
internal sealed class TimingSink : IPreviewFrameSink
{
    private readonly object _gate = new();
    private readonly List<(long Frame, long Timestamp)> _presents = [];

    public void Present(OwnedVideoFrame frame)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _presents.Add((frame.FrameIndex, now));
        }

        frame.Dispose();
    }

    public void Clear()
    {
    }

    public (int Count, double P50, double P99, double Max, int OverHalfFrame) Lateness(long playTimestamp, double fps)
    {
        List<(long Frame, long Timestamp)> played;
        lock (_gate)
        {
            played = _presents.Where(p => p.Timestamp >= playTimestamp).ToList();
        }

        if (played.Count < 2)
        {
            return (0, 0, 0, 0, 0);
        }

        var anchor = played[0];
        var late = played.Skip(1)
            .Select(p => Stopwatch.GetElapsedTime(anchor.Timestamp, p.Timestamp).TotalMilliseconds - ((p.Frame - anchor.Frame) * 1000.0 / fps))
            .Order()
            .ToList();
        var half = 500.0 / fps;
        return (late.Count, late[(int)Math.Round(0.5 * (late.Count - 1))], late[(int)Math.Round(0.99 * (late.Count - 1))], late[^1], late.Count(l => l > half));
    }
}

/// <summary>Export progress with timestamps, taken synchronously on the encode stage's thread.</summary>
internal sealed class ProgressRecorder : IProgress<ExportProgress>
{
    private readonly object _gate = new();
    private readonly List<(long Frames, long Timestamp)> _points = [];

    public void Report(ExportProgress value)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _points.Add((value.FramesWritten, now));
        }
    }

    /// <summary>Frames per second between the first report at >= 10 % and the last at <= 90 % of the frames.</summary>
    public double? SteadyFramesPerSecond(long total)
    {
        lock (_gate)
        {
            var from = _points.FirstOrDefault(p => p.Frames >= total * 0.1);
            var to = _points.LastOrDefault(p => p.Frames <= total * 0.9);
            return to.Frames > from.Frames && from.Timestamp != 0 ? (to.Frames - from.Frames) / Stopwatch.GetElapsedTime(from.Timestamp, to.Timestamp).TotalSeconds : null;
        }
    }
}

/// <summary>What else the machine was doing and how it was powered when the run started.</summary>
internal sealed record MachineState(int LogicalProcessors, double TotalRamGib, string PowerSource, string PowerPlan, string PowerMode, double BackgroundBusyProcessors,
    [property: System.Text.Json.Serialization.JsonIgnore] string[] LargestProcesses)   // never written to a results file
{
    public static unsafe MachineState Capture()
    {
        SystemPowerStatus power;
        var source = Win32.GetSystemPowerStatus(&power) == 0
            ? "unknown"
            : (power.AcLineStatus switch { 1 => "AC power", 0 => "battery", _ => "unknown power source" }) + (power.BatteryLifePercent <= 100 ? $" (battery {power.BatteryLifePercent} %)" : "");

        var plan = "unknown";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is not null)
            {
                var text = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10_000);
                var open = text.LastIndexOf('(');
                var close = text.LastIndexOf(')');
                plan = open >= 0 && close > open ? text[(open + 1)..close] : text.Trim();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        var mode = "unknown";
        try
        {
            if (Win32.PowerGetEffectiveOverlayScheme(out var overlay) == 0)
            {
                mode = overlay.ToString("D") switch
                {
                    "ded574b5-45a0-4f42-8737-46345c09c238" => "Best performance",
                    "961cc777-2547-4f9d-8174-7d86181b8a7a" => "Best power efficiency",
                    "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => "Better performance",
                    "00000000-0000-0000-0000-000000000000" => "Balanced",
                    _ => "overlay",
                } + $" (overlay {overlay:D})";
            }
        }
        catch (EntryPointNotFoundException)
        {
        }

        var before = SystemCpu.Times();
        var clock = Stopwatch.StartNew();
        Thread.Sleep(1000);
        var busy = SystemCpu.BusyProcessors(before, SystemCpu.Times(), clock.Elapsed.TotalSeconds);

        var self = Environment.ProcessId;
        var processes = Process.GetProcesses();
        string[] largest;
        try
        {
            largest = processes.Where(p => p.Id != self)
                .Select(p =>
                {
                    try
                    {
                        return (p.ProcessName, p.WorkingSet64);
                    }
                    catch (InvalidOperationException)
                    {
                        return (p.ProcessName, 0L);
                    }
                })
                .OrderByDescending(p => p.Item2)
                .Take(10)
                .Select(p => $"{p.ProcessName} {p.Item2 / Units.Mib:N0} MiB")
                .ToArray();
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }

        var memory = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        var totalRam = Win32.GlobalMemoryStatusEx(&memory) != 0 ? memory.TotalPhysical / Units.Gib : 0;
        return new MachineState(Environment.ProcessorCount, totalRam, source, plan, mode, busy, largest);
    }
}

/// <summary>Writes console output to a second writer as well (the B10 output file).</summary>
internal sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => first.Encoding;

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(string? value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        first.WriteLine(value);
        second.WriteLine(value);
    }

    public override void Flush()
    {
        first.Flush();
        second.Flush();
    }
}

/// <summary>MEMORYSTATUSEX, laid out as in sysinfoapi.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhysical;
    public ulong AvailablePhysical;
    public ulong TotalPageFile;
    public ulong AvailablePageFile;
    public ulong TotalVirtual;
    public ulong AvailableVirtual;
    public ulong AvailableExtendedVirtual;
}

/// <summary>PROCESS_MEMORY_COUNTERS_EX (psapi.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessMemoryCountersEx
{
    public uint Size;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivateUsage;
}

/// <summary>PROCESSENTRY32W (tlhelp32.h).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct ProcessEntry32
{
    public uint Size;
    public uint Usage;
    public uint ProcessId;
    public nint DefaultHeapId;
    public uint ModuleId;
    public uint Threads;
    public uint ParentProcessId;
    public int PriorityClassBase;
    public uint Flags;
    public fixed char ExeFile[260];
}

/// <summary>SYSTEM_POWER_STATUS (winbase.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    public byte AcLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

/// <summary>PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer, then PDH_FMT_COUNTERVALUE (status, padding, 8-byte union).</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct PdhCounterValueItem
{
    [FieldOffset(0)]
    public nint Name;

    [FieldOffset(8)]
    public uint Status;

    [FieldOffset(16)]
    public double DoubleValue;

    [FieldOffset(16)]
    public long LargeValue;
}

internal static unsafe class Win32
{
    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int GlobalMemoryStatusEx(MemoryStatusEx* buffer);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern nint OpenProcess(uint access, int inheritHandle, uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int CloseHandle(nint handle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int GetProcessTimes(nint process, long* creation, long* exit, long* kernel, long* user);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int GetSystemTimes(long* idle, long* kernel, long* user);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int K32GetProcessMemoryInfo(nint process, ProcessMemoryCountersEx* counters, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int Process32FirstW(nint snapshot, ProcessEntry32* entry);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int Process32NextW(nint snapshot, ProcessEntry32* entry);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern int GetSystemPowerStatus(SystemPowerStatus* status);

    [DllImport("powrprof.dll", ExactSpelling = true)]
    public static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
}

internal static class Pdh
{
    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    public static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    public static extern uint PdhCloseQuery(nint query);
}
