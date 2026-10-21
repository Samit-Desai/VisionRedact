using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Prism.Mvvm;
using Prism.Regions;
using VisionRedact.Application.Diagnostics;
using VisionRedact.Application.Media;
using VisionRedact.Application.Workspace;
using VisionRedact.Presentation.Wpf.Infrastructure;
using VisionRedact.Presentation.Wpf.Rendering;

namespace VisionRedact.Presentation.Wpf.Features.Diagnostics;

// one row, made once. only Value changes, and the view draws it w/ RenderOnlyText, so no layout pass for that
public sealed class MetricItem(string group, string label, string hint, bool startsSection = false) : BindableBase
{
    private string _value = "–";

    public string Group { get; } = group;

    public string Label { get; } = label;

    public string Hint { get; } = hint;

    // first row of a small section: the view draws a thin line above it
    public bool StartsSection { get; } = startsSection;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

// the Diagnostics tab: live, measured numbers, to prove and debug performance. samples 2x a second, only while
// the tab is shown.
// pull, not push: the pipelines keep cheap counters and this polls them. a notification per frame from the workers
// would cost Dispatcher time, the very thing being measured. other parts add rows through IDiagnosticsSource
public sealed class DiagnosticsViewModel : BindableBase, INavigationAware
{
    // readable numbers, little UI-thread work
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly VideoWorkspace _workspace;
    private readonly WriteableBitmapFramePresenter _presenter;
    private readonly IReadOnlyList<IDiagnosticsSource> _sources;
    // key -> row
    private readonly Dictionary<string, MetricItem> _items = [];
    // rates over 2 s, not per sample: one 0.5 s sample only counts whole frames, so the fps jumped around (RateMeter)
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(2);
    private readonly Stopwatch _stopwatch = new();
    private readonly Process _process = Process.GetCurrentProcess();
    // null while the tab is hidden
    private CancellationTokenSource? _sampling;
    private readonly RateMeter _presentRate = new(RateWindow);
    private readonly RateMeter _decodeRate = new(RateWindow);
    private readonly RateMeter _layoutRate = new(RateWindow);
    // from the main window's LayoutUpdated, once per pass. UI thread, only while the tab is open
    private long _layoutPasses;
    private UIElement? _layoutSource;
    // UI time in those passes, Stopwatch ticks. a pass runs inside WPF's render tick (a Render-priority Dispatcher
    // operation), so it's timed from that operation's start to LayoutUpdated. a bit high: includes the animation tick
    private readonly RateMeter _layoutTimeRate = new(RateWindow);
    private long _layoutTicks;
    private long _renderOperationStart;
    private DispatcherHooks? _hooks;
    private double _decodeAheadFill;

    public DiagnosticsViewModel(VideoWorkspace workspace, WriteableBitmapFramePresenter presenter, IEnumerable<IDiagnosticsSource> sources)
    {
        _workspace = workspace;
        _presenter = presenter;
        _sources = sources.ToArray();

        // every row up front, sampling only changes values. top to bottom: state, rates, time per frame in pipeline
        // order (decode -> redaction -> present), buffer and frames left out, layout
        Add("playback", "Playback", "State", "PlaybackSession state");
        Add("videoFps", "Playback", "Video frame rate", "The source's own frame rate: the target for the two rates below", startsSection: true);
        Add("decodeFps", "Playback", "Decode FPS", "Frames decoded by the playback producer per second (measured, 2 s average)");
        Add("presentFps", "Playback", "Presented FPS", "Frames written to the WriteableBitmap per second (measured, 2 s average)");
        Add("decodeMs", "Playback", "Avg decode time", "Mean ReadNext() time on the decode task. Preview redaction is not included: see the next row", startsSection: true);
        Add("redactMs", "Playback", "Preview redaction time", "Time to burn the redactions into each preview frame, on its own task while the next frame is decoded (last 128 frames: average, 95th percentile, slowest). The slower of decode time and this must stay under one frame interval, or frames are skipped");
        Add("presentMs", "Playback", "Present time (WritePixels)", "UI-thread time to copy each frame into the WriteableBitmap (last 128 frames: average, 95th percentile, slowest). This is the part a D3DImage presenter could take off the UI thread; WPF's own upload of the bitmap to the GPU happens afterwards on its render thread and is not included");
        Add("buffer", "Playback", "Decode-ahead buffer", "Bounded channel occupancy / capacity", startsSection: true);
        Add("lateDrops", "Playback", "Late frames dropped", "Frames left out to stay in real time. 'Skipped early': left out by the decode task before their pixels were converted or redacted, so dropping them saved that work; the rest arrived late and were dropped at presentation");
        Add("uiDrops", "Playback", "Preview frames replaced", "Frames superseded before the UI thread could draw them");
        Add("seekDiscards", "Playback", "Discarded by seek", "Buffered frames flushed because a seek made them stale");
        Add("layoutPasses", "Playback", "Layout passes / s", "WPF layout passes (measure/arrange) per second, from LayoutUpdated. Video frames, the playhead, the boxes and every changing text are drawn without layout, so this reads 0 while playing", startsSection: true);
        Add("layoutTime", "Playback", "Layout time (UI thread)", "UI-thread time spent in WPF layout passes: per second (2 s average) and per pass. Timed from the start of WPF's render tick to LayoutUpdated, so a slight upper bound");

        // source rows go between Playback and Memory
        foreach (var source in _sources)
        {
            foreach (var metric in source.Metrics)
            {
                Add(metric.Key, source.Group, metric.Label, metric.Hint, metric.StartsSection);
            }
        }

        Add("liveFrames", "Memory", "Live native frames", "OwnedVideoFrame instances not yet disposed (all pipelines)");
        Add("managed", "Memory", "Managed heap", "GC.GetTotalMemory(false): .NET objects only (storyboard tiles live here; video frames are native)", startsSection: true);
        Add("workingSet", "Memory", "Working set", "Process working set (managed + native + mapped)");
        Add("private", "Memory", "Private bytes", "Committed private memory: grows if native frames leak");
        Add("gc", "Memory", "GC collections (0/1/2)", "Gen2 counts rising during playback indicate LOH churn", startsSection: true);
    }

    public ObservableCollection<MetricItem> Items { get; } = [];

    // 0..1, for the little bar in the view
    public double DecodeAheadFill
    {
        get => _decodeAheadFill;
        private set => SetProperty(ref _decodeAheadFill, value);
    }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;

    public void OnNavigatedTo(NavigationContext navigationContext)
    {
        // never two loops at once
        _sampling?.Cancel();
        if (_layoutSource is not null)
        {
            _layoutSource.LayoutUpdated -= OnLayoutUpdated;
        }

        _layoutSource = System.Windows.Application.Current?.MainWindow;
        if (_layoutSource is not null)
        {
            _layoutSource.LayoutUpdated += OnLayoutUpdated;
        }

        UnhookDispatcher();
        _hooks = System.Windows.Application.Current?.Dispatcher.Hooks;
        if (_hooks is not null)
        {
            _hooks.OperationStarted += OnOperationStarted;
            _hooks.OperationCompleted += OnOperationEnded;
            _hooks.OperationAborted += OnOperationEnded;
        }

        _sampling = new CancellationTokenSource();
        // fire-and-forget on purpose: the loop runs until canceled and catches that itself
        _ = SampleLoopAsync(_sampling.Token);
    }

    // hidden: stop, a tab nobody looks at costs nothing
    public void OnNavigatedFrom(NavigationContext navigationContext)
    {
        if (_layoutSource is not null)
        {
            _layoutSource.LayoutUpdated -= OnLayoutUpdated;
            _layoutSource = null;
        }

        UnhookDispatcher();
        _sampling?.Cancel();
        _sampling?.Dispose();
        _sampling = null;
    }

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        // UI thread. PeriodicTimer doesn't block it and never stacks ticks if a sample is slow;
        // each await comes back on the UI thread, so Sample may touch the bound rows
        using var timer = new PeriodicTimer(Interval);
        _stopwatch.Restart();
        _presentRate.Reset();
        _decodeRate.Reset();
        _layoutRate.Reset();
        _layoutTimeRate.Reset();
        // one right away, so the tab isn't empty for half a second
        Sample();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Sample();
            }
        }
        catch (OperationCanceledException)
        {
            // the normal end: the tab was hidden
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        _layoutPasses++;
        if (_renderOperationStart != 0)
        {
            var now = Stopwatch.GetTimestamp();
            _layoutTicks += now - _renderOperationStart;
            _renderOperationStart = now;   // a second pass in the same tick is timed from the end of the first
        }
    }

    // the presenter's own Render-priority callbacks run no layout, so they never reach LayoutUpdated and don't count
    private void OnOperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        if (e.Operation.Priority == DispatcherPriority.Render)
        {
            _renderOperationStart = Stopwatch.GetTimestamp();
        }
    }

    private void OnOperationEnded(object? sender, DispatcherHookEventArgs e) => _renderOperationStart = 0;

    private void UnhookDispatcher()
    {
        if (_hooks is not null)
        {
            _hooks.OperationStarted -= OnOperationStarted;
            _hooks.OperationCompleted -= OnOperationEnded;
            _hooks.OperationAborted -= OnOperationEnded;
            _hooks = null;
        }

        _renderOperationStart = 0;
    }

    private void Sample()
    {
        var now = _stopwatch.Elapsed;
        var c = CultureInfo.CurrentCulture;

        // presenter: frames really drawn, and frames replaced before the UI thread got to them (Interlocked counters)
        Set("presentFps", Rate(_presentRate.Add(_presenter.FramesRendered, now), c));
        Set("presentMs", Durations(_presenter.PresentTimes, "0.00", c));
        Set("uiDrops", _presenter.FramesDropped.ToString("N0", c));
        var passes = _layoutRate.Add(_layoutPasses, now);
        Set("layoutPasses", Rate(passes, c));
        var micros = _layoutTimeRate.Add(_layoutTicks * 1_000_000 / Stopwatch.Frequency, now);
        Set("layoutTime", LayoutTime(micros, passes, c));

        if (_workspace.CurrentSession is { } session)
        {
            // one snapshot, so all playback rows are from the same moment
            var stats = session.GetStatistics();
            Set("playback", session.State.ToString());
            Set("videoFps", session.Metadata.FramesPerSecond.ToString("0.00", c));
            Set("decodeFps", Rate(_decodeRate.Add(stats.FramesDecoded, now), c));
            Set("decodeMs", stats.AverageDecodeTime.TotalMilliseconds.ToString("0.0 ms", c));
            Set("redactMs", Durations(stats.PreviewProcessTime, "0.0", c));
            Set("buffer", $"{stats.DecodeAheadCount} / {stats.DecodeAheadCapacity}");
            DecodeAheadFill = stats.DecodeAheadCapacity == 0 ? 0 : (double)stats.DecodeAheadCount / stats.DecodeAheadCapacity;
            Set("lateDrops", stats.FramesDroppedLate == 0
                ? "0"
                : string.Create(c, $"{stats.FramesDroppedLate:N0} ({stats.FramesSkippedEarly:N0} skipped early)"));
            Set("seekDiscards", stats.FramesDiscardedBySeek.ToString("N0", c));
        }
        else
        {
            Set("playback", "No video");
        }

        foreach (var source in _sources)
        {
            foreach (var (key, value) in source.Sample(now))
            {
                Set(key, value);
            }
        }

        // Process caches its numbers, Refresh() re-reads them.
        // LiveInstances = the leak check, it should fall back when playback stops
        _process.Refresh();
        Set("liveFrames", OwnedVideoFrame.LiveInstances.ToString("N0", c));
        Set("managed", DisplayFormat.FileSize(GC.GetTotalMemory(false)));
        Set("workingSet", DisplayFormat.FileSize(_process.WorkingSet64));
        Set("private", DisplayFormat.FileSize(_process.PrivateMemorySize64));
        Set("gc", $"{GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}");
    }

    // a dash until the window has two samples
    internal static string Rate(double? perSecond, IFormatProvider format) => perSecond?.ToString("0.0", format) ?? "–";

    // ms per second, plus ms per pass when there were passes. both over the same 2 s window
    internal static string LayoutTime(double? microsPerSecond, double? passesPerSecond, IFormatProvider format)
    {
        if (microsPerSecond is not { } us)
        {
            return "–";
        }

        var text = (us / 1000).ToString("0.00 'ms/s'", format);
        return passesPerSecond is { } passes && passes >= 0.5
            ? text + " · " + (us / 1000 / passes).ToString("0.000 'ms per pass'", format)
            : text;
    }

    // avg, p95 and max, or a dash before the first sample
    internal static string Durations(DurationSummary d, string number, IFormatProvider format) => d.Samples == 0
        ? "–"
        : string.Format(format, "avg {0} · p95 {1} · max {2} ms",
            d.Average.TotalMilliseconds.ToString(number, format), d.P95.TotalMilliseconds.ToString(number, format), d.Max.TotalMilliseconds.ToString(number, format));

    private void Add(string key, string group, string label, string hint, bool startsSection = false)
    {
        var item = new MetricItem(group, label, hint, startsSection);
        _items[key] = item;
        Items.Add(item);
    }

    // unknown keys are ignored, so a source can't crash the tab
    private void Set(string key, string value)
    {
        if (_items.TryGetValue(key, out var item))
        {
            item.Value = value;
        }
    }
}

// rows from one part of the app (the AI scan, the timeline previews) w/o the tab knowing it: register a source in
// DI and its rows show up. Sample runs on the UI thread twice a second, so read cheap counters only
public interface IDiagnosticsSource
{
    // heading above this source's rows, e.g. "AI analysis"
    string Group { get; }

    // created once, in display order
    IReadOnlyList<DiagnosticsMetric> Metrics { get; }

    /// <summary>Current (key, formatted value) pairs. The timestamp lets a source compute rates.</summary>
    IEnumerable<(string Key, string Value)> Sample(TimeSpan timestamp);
}

public sealed record DiagnosticsMetric(string Key, string Label, string Hint, bool StartsSection = false);
