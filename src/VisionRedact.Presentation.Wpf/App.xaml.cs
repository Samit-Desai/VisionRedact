using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Prism.DryIoc;
using Prism.Ioc;
using Prism.Mvvm;
using Prism.Regions;
using VisionRedact.Application.Analysis;
using VisionRedact.Application.Detections;
using VisionRedact.Application.Export;
using VisionRedact.Application.Jobs;
using VisionRedact.Application.Media;
using VisionRedact.Application.People;
using VisionRedact.Application.Playback;
using VisionRedact.Application.Preview;
using VisionRedact.Application.Projects;
using VisionRedact.Application.Redaction;
using VisionRedact.Application.Tracking;
using VisionRedact.Application.Workspace;
using VisionRedact.Infrastructure.AI;
using VisionRedact.Infrastructure.Persistence;
using VisionRedact.Infrastructure.Video;
using VisionRedact.Presentation.Wpf.Features.Detections;
using VisionRedact.Presentation.Wpf.Features.Diagnostics;
using VisionRedact.Presentation.Wpf.Features.Dialogs;
using VisionRedact.Presentation.Wpf.Features.Inspector;
using VisionRedact.Presentation.Wpf.Features.People;
using VisionRedact.Presentation.Wpf.Features.Player;
using VisionRedact.Presentation.Wpf.Features.Redaction;
using VisionRedact.Presentation.Wpf.Infrastructure;
using VisionRedact.Presentation.Wpf.Rendering;
using VisionRedact.Presentation.Wpf.Shell;

namespace VisionRedact.Presentation.Wpf;

// composition root: the only file that names the real adapters (Emgu, ONNX, FFmpeg, JSON).
// everything else asks for an interface or an Application class, so swapping a library = one line here
public partial class App : PrismApplication
{
    // made before Prism's container, so even a broken container gets logged
    private ILoggerFactory? _loggerFactory;
    // at most one error box per quiet period, the errors in between are only counted
    private const long ErrorDialogQuietPeriodMs = 10_000;
    private ILogger? _logger;
    private bool _isShowingError;
    // trick: MinValue / 2, not MinValue, so now - this can't overflow and the very first error gets its box
    private long _lastErrorDialogAt = long.MinValue / 2;
    private int _suppressedErrors;
    // "Type: message" -> times seen. UI thread only (DispatcherUnhandledException runs there), so no lock
    private readonly Dictionary<string, int> _errorCounts = new(StringComparer.Ordinal);

    // first code to run: logging, error handlers, OpenCV threads, then Prism
    protected override void OnStartup(StartupEventArgs e)
    {
        // logging first, so everything after it is logged
        _loggerFactory = LoggingSetup.CreateLoggerFactory();
        _logger = _loggerFactory.CreateLogger<App>();
        // before any window exists
        RegisterGlobalExceptionHandlers();
        // 64-bit matters for the native libs (OpenCV, ONNX Runtime)
        _logger.LogInformation("VisionRedact Studio starting (64-bit process: {Is64Bit}, debugger attached: {Debugger}).", Environment.Is64BitProcess, System.Diagnostics.Debugger.IsAttached);
        // cap OpenCV's pool before its first call. why: OpenCvThreads, docs/Performance.md optimization 8
        _logger.LogInformation("OpenCV threads: {Threads}.", OpenCvThreads.Configure());

        base.OnStartup(e); // Prism: ConfigureViewModelLocator -> RegisterTypes -> CreateShell -> OnInitialized
    }

    // resolving the window builds its view model and everything they need
    protected override Window CreateShell() => Container.Resolve<ShellWindow>();

    // singletons almost everywhere: one open video, one player
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // open generic, so any class can ask for its own ILogger<T>. TimeProvider so tests can fake the clock
        containerRegistry.RegisterInstance(_loggerFactory!);
        containerRegistry.Register(typeof(ILogger<>), typeof(Logger<>));
        containerRegistry.RegisterInstance(TimeProvider.System);

        // options as instances: DryIoc would try to fill a record's copy ctor, again and again
        containerRegistry.RegisterInstance(PlaybackOptions.Default);
        containerRegistry.RegisterInstance(DetectorOptions.Default);
        containerRegistry.RegisterInstance(AnalysisOptions.Default);
        containerRegistry.RegisterInstance(FramePreviewOptions.Default);

        // behind interfaces so view models don't need a real Dispatcher or real dialogs.
        // TODO: no test project covers Presentation yet, so nothing fakes them. ok for now
        containerRegistry.RegisterSingleton<IUiThread, DispatcherUiThread>();
        containerRegistry.RegisterSingleton<IFileDialogService, Win32FileDialogService>();
        containerRegistry.RegisterDialog<ErrorDialogView, ErrorDialogViewModel>(DialogNames.Error);

        // the Inspector tabs (region navigation by name), each with its view model
        containerRegistry.RegisterForNavigation<MetadataView, MetadataViewModel>(ViewNames.Metadata);
        containerRegistry.RegisterForNavigation<DetectionsView, DetectionsViewModel>(ViewNames.Detections);
        containerRegistry.RegisterForNavigation<RedactionView, RedactionViewModel>(ViewNames.Redaction);
        containerRegistry.RegisterForNavigation<PeopleView, PeopleViewModel>(ViewNames.People);
        containerRegistry.RegisterForNavigation<DiagnosticsView, DiagnosticsViewModel>(ViewNames.Diagnostics);

        // UI-thread state shared between features
        containerRegistry.RegisterSingleton<DetectionPreferences>();
        containerRegistry.RegisterSingleton<RedactionEditorState>();
        // each one adds a section, DiagnosticsViewModel gets them all as IEnumerable
        containerRegistry.RegisterSingleton<IDiagnosticsSource, AnalysisDiagnosticsSource>();
        containerRegistry.RegisterSingleton<IDiagnosticsSource, PreviewDiagnosticsSource>();

        // remember: ONE object for both. the engine writes frames into IPreviewFrameSink, the player view shows its
        // bitmap. two instances = frames go into one while the screen shows the other
        containerRegistry.RegisterManySingleton<WriteableBitmapFramePresenter>(typeof(IPreviewFrameSink), typeof(WriteableBitmapFramePresenter));

        // adapters behind Application ports: OpenCV (Emgu), ONNX Runtime, DXGI for the GPU list
        containerRegistry.RegisterSingleton<IMediaRuntimeProbe, EmguRuntimeProbe>();
        containerRegistry.RegisterSingleton<IVideoDecoderFactory, EmguVideoDecoderFactory>();
        containerRegistry.RegisterSingleton<IFrameImaging, EmguFrameImaging>();
        containerRegistry.RegisterSingleton<FramePreviewService>();
        containerRegistry.RegisterSingleton<IDetectionModelCatalog, OnnxModelCatalog>();
        containerRegistry.RegisterSingleton<IObjectDetectorFactory, OnnxDetectorFactory>();
        containerRegistry.RegisterSingleton<IInferenceDeviceCatalog, DxgiAdapterCatalog>();

        // the list of effects is policy, so it's written out here
        containerRegistry.RegisterInstance<IRedactionProcessor>(new RedactionProcessor([new GaussianBlurEffect(), new PixelateEffect(), new SolidBlackEffect()]));

        // the open video's working set
        containerRegistry.RegisterSingleton<IDetectionRepository, InMemoryDetectionRepository>();
        containerRegistry.RegisterSingleton<IRedactionRegionStore, RedactionRegionStore>();
        // same as the presenter: the session gets IPreviewFrameProcessor, RedactionEditorState flips IsEnabled on it
        containerRegistry.RegisterManySingleton<RedactionPreviewProcessor>(typeof(IPreviewFrameProcessor), typeof(RedactionPreviewProcessor));
        containerRegistry.RegisterSingleton<IProcessingJobManager, ProcessingJobManager>();
        containerRegistry.RegisterSingleton<DetectorHost>();
        containerRegistry.RegisterSingleton<FrameDetectionService>();
        // a tracker has state, so a fresh one per scan. KCF follows a drawn region by its px ("track region")
        containerRegistry.RegisterInstance<Func<IObjectTracker>>(() => new IouObjectTracker());
        containerRegistry.RegisterInstance<IVisualTrackerFactory>(new EmguVisualTrackerFactory(VisualTrackerAlgorithm.Kcf));
        containerRegistry.RegisterSingleton<AnalysisService>();
        containerRegistry.RegisterSingleton<VideoWorkspace>();
        // export: FFmpeg (H.264 + audio, first encoder that passes the probe) if ffmpeg\ is next to the exe,
        // else (and for AVI) OpenCV's writer. callers only see one IVideoWriterFactory.
        // a lambda b/c the fallback is a concrete class, not a registered interface
        containerRegistry.RegisterSingleton<IVideoWriterFactory>(c => new FfmpegVideoWriterFactory(
            new EmguVideoWriterFactory(c.Resolve<ILogger<EmguVideoWriterFactory>>()),
            c.Resolve<ILogger<FfmpegVideoWriterFactory>>()));
        containerRegistry.RegisterSingleton<IVideoExportService, VideoExportService>();
        containerRegistry.RegisterSingleton<IProjectRepository, JsonProjectRepository>();
        containerRegistry.RegisterSingleton<ProjectService>();

        // people: face fingerprints (YuNet + ArcFace) -> groups of tracks. the store keeps the index + its undo history
        containerRegistry.RegisterInstance(IdentityOptions.Default);
        containerRegistry.RegisterSingleton<IFaceEmbedderFactory, ArcFaceEmbedderFactory>();
        containerRegistry.RegisterSingleton<IPeopleStore, InMemoryPeopleStore>();
        containerRegistry.RegisterSingleton<PeopleService>();
        containerRegistry.RegisterSingleton<FaceThumbnails>();
        containerRegistry.RegisterSingleton<IUserPrompt, MessageBoxUserPrompt>();
    }

    protected override void ConfigureViewModelLocator()
    {
        base.ConfigureViewModelLocator();

        // explicit View -> ViewModel, not a naming convention: easy to find, safe to rename.
        // the tabs and the error dialog get theirs from RegisterForNavigation / RegisterDialog
        ViewModelLocationProvider.Register<PlayerView, PlayerViewModel>();
    }

    // the shell exists now, base shows it
    protected override void OnInitialized()
    {
        // view discovery: Prism puts a PlayerView in the Player region when the region is created.
        // the shell XAML only has the empty placeholder, it doesn't know the view type
        var regionManager = Container.Resolve<IRegionManager>();
        regionManager.RegisterViewWithRegion(RegionNames.Player, typeof(PlayerView));
        base.OnInitialized();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("VisionRedact Studio exiting with code {Code}.", e.ApplicationExitCode);
        base.OnExit(e);
        // last, so the file gets everything logged during shutdown
        _loggerFactory?.Dispose();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // last line of defense. expected failures are handled where they happen, these log and keep WPF alive
        // UI thread: event handlers, bindings, layout, render
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // a faulted task nobody awaited: log it, mark it observed
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
        // anything else still unhandled: a background thread, or a UI error we let through before the shell
        // existed. .NET ends the process, we can only log it
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception on a non-UI thread (terminating: {Terminating}).", args.IsTerminating);
    }

    // escaped on the UI thread: log it (de-duplicated), decide if the app can go on, show at most one error box
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // a layout / render error repeats every frame: the first few in full, then only a count now and then
        var key = e.Exception.GetType().FullName + ": " + e.Exception.Message;
        var occurrences = _errorCounts[key] = _errorCounts.GetValueOrDefault(key) + 1;
        if (occurrences <= 3)
        {
            _logger?.LogError(e.Exception, "Unhandled exception on the UI thread.");
        }
        else if (occurrences % 500 == 0)
        {
            _logger?.LogError("Unhandled exception on the UI thread repeated {Count} times: {Error}", occurrences, key);
        }

        // the "wrong Dispatcher" problem shows up as an ArgumentException. the first time we see one, log every
        // object that belongs to another Dispatcher
        if (occurrences == 1 && e.Exception is ArgumentException && MainWindow is { } window)
        {
            DispatcherAffinityProbe.LogForeignObjects(window, this, _logger);
        }

        var hasShell = MainWindow is { IsLoaded: true };

        // no shell yet = nothing to keep alive: let the process end, not a windowless zombie
        e.Handled = hasShell;

        // never stack dialogs: a layout error repeats every frame, and each modal box pumps messages, which runs
        // the broken layout again
        var now = Environment.TickCount64;
        if (_isShowingError || now - _lastErrorDialogAt < ErrorDialogQuietPeriodMs)
        {
            _suppressedErrors++;
            return;
        }

        // MessageBox runs a nested message loop, so another error can come in while it's open
        _isShowingError = true;
        try
        {
            var suppressed = _suppressedErrors > 0 ? $"{Environment.NewLine}({_suppressedErrors} similar errors were logged without a dialog.)" : string.Empty;
            _suppressedErrors = 0;
            var message = "An unexpected error occurred. The details were written to the log file:" + Environment.NewLine
                + LoggingSetup.LogDirectory + Environment.NewLine + Environment.NewLine + e.Exception.Message + suppressed;

            // only seen under the Visual Studio debugger: its XAML tools (WpfTap) leave a second Dispatcher on the
            // UI thread, so new objects don't match the window's one
            if (Dispatcher.CurrentDispatcher != Dispatcher)
            {
                message += Environment.NewLine + Environment.NewLine
                    + "The UI thread now has a second WPF Dispatcher. This happens when Visual Studio's XAML UI debugging tools "
                    + "are attached. Turn off Tools > Options > Debugging > General > \"Enable UI Debugging Tools for XAML\", "
                    + "or run without debugging (Ctrl+F5).";
            }
            if (hasShell)
            {
                MessageBox.Show(MainWindow!, message, "VisionRedact Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(message, "VisionRedact Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _isShowingError = false;
            _lastErrorDialogAt = Environment.TickCount64;
        }
    }
}