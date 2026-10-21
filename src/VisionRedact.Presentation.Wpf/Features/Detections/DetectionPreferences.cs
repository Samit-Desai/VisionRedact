using System.Collections.ObjectModel;
using Prism.Mvvm;
using VisionRedact.Application.Analysis;
using VisionRedact.Application.Detections;

namespace VisionRedact.Presentation.Wpf.Features.Detections;

public sealed class ModelOption(DetectionModel model) : BindableBase
{
    private bool _isSelected;

    public DetectionModel Model { get; } = model;

    public string Name => Model.DisplayName;

    public string Details => Model.IsAvailable
        ? $"{Model.ClassSummary} · {Model.Format} · {Model.Notes}"
        : $"Not installed — run scripts/download-model.ps1 ({System.IO.Path.GetFileName(Model.FilePath)}).";

    public bool IsAvailable => Model.IsAvailable;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value && IsAvailable);
    }
}

public sealed record SamplingOption(string Label, Func<double, IFrameSamplingStrategy> Create)
{
    public override string ToString() => Label;
}

// which models, which device, how densely to sample. one singleton, so the player's "detect this frame" and the
// Detections tab's "analyze video" use the same choices
public sealed class DetectionPreferences : BindableBase
{
    private readonly IDetectionModelCatalog _catalog;
    private readonly IInferenceDeviceCatalog _devices;
    private readonly DetectorHost _host;
    private SamplingOption _sampling;
    private InferenceTarget _target = InferenceTarget.Cpu;

    public DetectionPreferences(IDetectionModelCatalog catalog, IInferenceDeviceCatalog devices, DetectorHost host)
    {
        _catalog = catalog;
        _devices = devices;
        _host = host;
        SamplingOptions =
        [
            new("Every frame (slowest, full coverage)", _ => new EveryFrameSampling()),
            new("1 of every 2 frames", _ => new EveryNthFrameSampling(2)),
            new("10 frames per second", fps => new TargetRateSampling(10, fps)),
            new("5 frames per second", fps => new TargetRateSampling(5, fps)),
            new("2 frames per second (fastest)", fps => new TargetRateSampling(2, fps)),
        ];
        _sampling = SamplingOptions[3];
    }

    public ObservableCollection<ModelOption> Models { get; } = [];

    public IReadOnlyList<SamplingOption> SamplingOptions { get; }

    public SamplingOption Sampling
    {
        get => _sampling;
        set => SetProperty(ref _sampling, value);
    }

    public ObservableCollection<InferenceTarget> Targets { get; } = [InferenceTarget.Cpu];

    // for models loaded from now on. a running scan keeps its device; each (model, device) pair is its own session
    public InferenceTarget Target
    {
        get => _target;
        set
        {
            if (value is not null && SetProperty(ref _target, value))
            {
                _host.Target = value;
            }
        }
    }

    public IReadOnlyList<DetectionModel> SelectedModels => Models.Where(m => m.IsSelected).Select(m => m.Model).ToArray();

    // reads ONNX metadata, so off the UI thread. keeps the ticked models
    public async Task RefreshAsync()
    {
        var selected = Models.Where(m => m.IsSelected).Select(m => m.Model.Id).ToHashSet();
        var models = await Task.Run(_catalog.GetModels);
        if (Targets.Count <= 1)
        {
            // DXGI is native too: off the UI thread. default = the GPU w/ the most dedicated memory
            // (the discrete one on a laptop that has two)
            var targets = await Task.Run(_devices.GetTargets);
            Targets.Clear();
            foreach (var t in targets)
            {
                Targets.Add(t);
            }

            Target = targets.Where(t => t.Device == InferenceDevice.DirectML).MaxBy(t => t.DedicatedMemoryMb) ?? InferenceTarget.Cpu;
        }


        Models.Clear();
        foreach (var model in models)
        {
            Models.Add(new ModelOption(model) { IsSelected = selected.Contains(model.Id) });
        }

        if (!Models.Any(m => m.IsSelected))
        {
            // nothing ticked: a YOLO face model if there is one (UltraFace misses small faces), else any installed one
            var option = (Models.FirstOrDefault(m => m.IsAvailable && m.Model.Format == DetectionModelFormat.Yolo && m.Model.Classes.Values.Contains(Domain.Detections.ObjectClass.Face))
             ?? Models.FirstOrDefault(m => m.IsAvailable));
            if (option != null) option.IsSelected = true;
        }
    }
}
