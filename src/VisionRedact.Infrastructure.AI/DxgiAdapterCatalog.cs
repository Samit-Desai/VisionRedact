using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionRedact.Application.Detections;

namespace VisionRedact.Infrastructure.AI;

// lists the GPUs the way DirectML numbers them: its device_id is the EnumAdapters1 index, and WMI & co. may list
// them in another order (ADR-0017). DetectionPreferences ("Run on") -> InferenceTarget.AdapterIndex -> DirectML.
// raw COM through vtable slots, no interop package. a failed HRESULT or a native-load error = CPU only, never a
// failed startup
public sealed unsafe class DxgiAdapterCatalog : IInferenceDeviceCatalog
{
    // DXGI_ERROR_NOT_FOUND = past the last adapter
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint SoftwareAdapterFlag = 2; // DXGI_ADAPTER_FLAG_SOFTWARE (e.g. the Basic Render Driver)

    // IDXGIFactory1 = {770aae78-f26f-4dba-a829-253c83d1b387}
    private static readonly Guid IidDxgiFactory1 = new(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);

    private readonly ILogger _logger;

    public DxgiAdapterCatalog(ILogger<DxgiAdapterCatalog>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The GPU with the most dedicated memory (usually the discrete one), else the CPU.</summary>
    // TODO: only the benchmarks call this, DetectionPreferences has the same rule inline. ok for now
    public static InferenceTarget Preferred(IReadOnlyList<InferenceTarget> targets) =>
        targets.Where(t => t.Device == InferenceDevice.DirectML).MaxBy(t => t.DedicatedMemoryMb) ?? InferenceTarget.Cpu;

    /// <summary>CPU first (always there), then every hardware GPU in DirectML's own index order.</summary>
    public IReadOnlyList<InferenceTarget> GetTargets()
    {
        // DXGI is Windows only
        var targets = new List<InferenceTarget> { InferenceTarget.Cpu };
        if (!OperatingSystem.IsWindows())
        {
            return targets;
        }

        IntPtr factory = IntPtr.Zero;
        try
        {
            // HRESULT < 0 = failed
            var iid = IidDxgiFactory1;
            if (CreateDXGIFactory1(&iid, &factory) < 0 || factory == IntPtr.Zero)
            {
                return targets;
            }

            // adapter 0, 1, 2... until DXGI says not found. the loop index IS DirectML's device_id
            for (uint index = 0; ; index++)
            {
                IntPtr adapter = IntPtr.Zero;

                // vtable: IUnknown(3) + IDXGIObject(4) + IDXGIFactory(5) -> EnumAdapters1 is slot 12
                var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(factory)[12];
                var hr = enumAdapters1(factory, index, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0)
                {
                    break;
                }

                try
                {
                    // IDXGIAdapter1: IUnknown(3) + IDXGIObject(4) + IDXGIAdapter(3) -> GetDesc1 is slot 10.
                    // software adapters are just the CPU again, skip them
                    DxgiAdapterDesc1 desc;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DxgiAdapterDesc1*, int>)VTable(adapter)[10];
                    if (getDesc1(adapter, &desc) >= 0 && (desc.Flags & SoftwareAdapterFlag) == 0)
                    {
                        // fixed 128-char buffer, padded w/ '\0'
                        var name = new string(desc.Description).TrimEnd('\0');
                        var memoryMb = (long)(desc.DedicatedVideoMemory / (1024 * 1024));

                        // same name + memory twice = the same GPU, keep the first (lower index)
                        if (!targets.Any(t => t.Name == name && t.DedicatedMemoryMb == memoryMb))
                        {
                            targets.Add(new InferenceTarget(InferenceDevice.DirectML, (int)index, name, memoryMb));
                        }
                    }
                }
                finally
                {
                    // release each adapter, even if reading it failed
                    Release(adapter);
                }
            }
        }
        // only native-load failures (no dxgi.dll, missing export, native crash). a failed HRESULT doesn't throw, it
        // just ends the list above
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            _logger.LogWarning(ex, "GPU enumeration failed; inference will use the CPU.");
        }
        finally
        {
            Release(factory);
        }

        return targets;
    }

    // a COM object's first field points to its vtable
    private static IntPtr* VTable(IntPtr comObject) => *(IntPtr**)comObject;

    private static void Release(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)VTable(comObject)[2]; // IUnknown::Release
            release(comObject);
        }
    }

    // the one plain export we need from dxgi.dll, the rest goes through vtables
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(Guid* riid, IntPtr* factory);

    // DXGI_ADAPTER_DESC1 exactly as in dxgi.h: native code writes straight into it, so order and sizes must match
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}
