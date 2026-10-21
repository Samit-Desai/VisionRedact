namespace VisionRedact.Application.Detections;

// where inference runs: the CPU or one GPU through DirectML. DetectionPreferences picks the GPU w/ the most memory.
// AdapterIndex = the DXGI adapter index, which is DirectML's device id (-1 for the CPU).
// DedicatedMemoryMb is 0 for the CPU and for GPUs that report none
public sealed record InferenceTarget(InferenceDevice Device, int AdapterIndex, string Name, long DedicatedMemoryMb = 0)
{
    public static InferenceTarget Cpu { get; } = new(InferenceDevice.Cpu, -1, "CPU");

    // DetectorHost cache key: the same model on another device is another session
    public string Key => Device == InferenceDevice.Cpu ? "cpu" : $"dml:{AdapterIndex}";

    public override string ToString() => Device == InferenceDevice.Cpu
        ? "CPU"
        : DedicatedMemoryMb > 0 ? $"GPU: {Name} ({DedicatedMemoryMb / 1024.0:0.#} GB)" : $"GPU: {Name}";
}

// DxgiAdapterCatalog
public interface IInferenceDeviceCatalog
{
    // CPU first, then each hardware GPU in DirectML's order. if DXGI can't be loaded you just get the CPU
    IReadOnlyList<InferenceTarget> GetTargets();
}
