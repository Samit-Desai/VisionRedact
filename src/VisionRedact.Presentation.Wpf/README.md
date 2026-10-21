# VisionRedact.Presentation.Wpf

Prism 8 / DryIoc composition root and WPF UI (`VisionRedactStudio.exe`, `net10.0-windows10.0.20348.0`, x64).
References every other `src/` project; `Prism.DryIoc`, `Serilog.Extensions.Logging`, `Serilog.Sinks.File`. Copies
`models/*.onnx` next to the exe.

```text
App (PrismApplication)      registrations, global exception handlers (rate-limited dialog, de-duplicated logging), logging
Shell/                      ShellWindow + ShellViewModel (open video/project, save, export, status, runtime banner, async shutdown)
Features/Player/            PlayerView + PlayerViewModel (transport, timeline, scrubbing; .Overlays: boxes, markers,
                            region editing, "detect this frame", 2 Hz overlay tick)
Features/Detections/        DetectionsView + DetectionsViewModel (models, device, sampling, analysis job, virtualized
                            review list drained at 4 Hz, approve/reject per track); DetectionPreferences
Features/Redaction/         RedactionView + RedactionViewModel (manual regions, effects, "track region" job); RedactionEditorState
Features/Diagnostics/       DiagnosticsView + DiagnosticsViewModel (PeriodicTimer 2 Hz while visible; IDiagnosticsSource,
                            AnalysisDiagnosticsSource incl. execution provider)
Features/Inspector/         MetadataView + MetadataViewModel (DataTemplate-driven metadata list)
Features/Dialogs/           ErrorDialog (Prism IDialogService)
Controls/                   VideoTimelineControl + TimelineTrack (lookless, DPs, routed events, commands, OnRender markers),
                            VideoSurface + BoxOverlay (frame, overlay and input layers for draw/move/resize)
Rendering/                  WriteableBitmapFramePresenter — the Dispatcher boundary for frames (single slot, one BeginInvoke)
Infrastructure/             IUiThread (Post only), AsyncCommand, IFileDialogService, DispatcherAffinityProbe, logging, formatting
Events/                     coarse EventAggregator events: VideoOpened, SelectedDetectionChanged, SeekRequested, JobCompleted
Themes/                     Theme.xaml (palette, ControlTemplates, styles), Generic.xaml (custom control templates)
```

Rules: ViewModels coordinate, never decode or infer; no per-frame or per-detection EventAggregator traffic; background
code reaches the UI only through `IUiThread.Post`, the presenter, or queues drained by UI-thread timers.

Debugging: run with *Enable UI Debugging Tools for XAML* turned off in Visual Studio, see
[../../docs/ErrorHandling.md](../../docs/ErrorHandling.md#visual-studio-xaml-tools-and-a-second-dispatcher).
