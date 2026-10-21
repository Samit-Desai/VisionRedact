# ADR-0017: GPU Inference Through the DirectML Execution Provider

## Context
On the CPU, YOLO11n takes 23.4 ms p50 per frame and every-frame analysis runs at 39 frames/s with the analysis
channel full (4/4): inference is the bottleneck while a GPU sits idle. Target machines are Windows workstations and
laptops with NVIDIA, AMD or Intel GPUs, often more than one (discrete + integrated). The app must still start and
work on a machine with no usable GPU.

## Options
1. CPU only.
2. CUDA execution provider — fastest on NVIDIA, but NVIDIA only and requires matching CUDA/cuDNN installs.
3. DirectML execution provider — any DirectX 12 GPU, no extra install, ships as one NuGet package.
4. Windows ML — Microsoft's successor path. It ships ONNX Runtime with the same APIs, but a different deployment
   model (vendor execution providers are downloaded on demand through the Windows ML package and need Windows 11
   24H2), and its generic GPU path is still
   DirectML.

## Decision
Option 3, with the CPU as default fallback. The package is `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4
([`Directory.Packages.props`](../../Directory.Packages.props)), which contains both the CPU and DirectML execution
providers. [`DxgiAdapterCatalog`](../../src/VisionRedact.Infrastructure.AI/DxgiAdapterCatalog.cs) enumerates GPUs with
`IDXGIFactory1::EnumAdapters1` through a minimal COM vtable call; the enumeration index is the DirectML device id.
Software adapters are excluded, duplicates removed, and `Preferred()` picks the GPU with the most dedicated memory.
[`InferenceTarget`](../../src/VisionRedact.Application/Detections/InferenceTarget.cs) carries the device and adapter
index; [`OnnxDetectorFactory`](../../src/VisionRedact.Infrastructure.AI/OnnxDetectorFactory.cs) appends the DirectML
provider (memory pattern off, sequential execution). If the provider is unavailable or session creation fails on the
GPU, it falls back to the CPU and says so in `DetectorInfo.ExecutionProvider`, shown in the diagnostics panel.
[`DetectorHost`](../../src/VisionRedact.Application/Detections/DetectorHost.cs) caches one session per (model, device).

## Why
DirectML covers every GPU vendor the target machines have without asking users to install CUDA. DXGI order is the
only reliable mapping from "NVIDIA RTX…" to a DirectML device id; WMI and other APIs may order adapters differently.
Enumeration failure must never block startup, so any error yields CPU only.

## Benefits
Measured (Intel Core Ultra 9 185H, RTX 3500 Ada, Intel Arc iGPU): YOLO11n p50 CPU 23.4 ms, Arc 14.2 ms, RTX 4.7 ms
(5×); every-frame pipeline 39 → 152 frames/s. UltraFace CPU 5.5 / Arc 3.7 / RTX 2.4 ms. Both GPUs reproduce the
ONNX Model Zoo reference outputs for UltraFace within 3e-7. In-app on the MTS at 5 samples/s: CPU 29.9 AI frames/s with the queue at 4/4
(inference-bound); RTX 58.5 AI frames/s, 726 decoder frames/s, queue 0/4 (decode-bound).

## Costs
DirectML is in sustained engineering: its latest package is 1.24.x while the CPU-only package is at 1.31, so the app
stays on an older ONNX Runtime. A DirectML session raises private bytes to ~1 GB (GPU staging/upload heaps; ~184–238 MB
with the CPU provider). The first inference after load is slower (shader compilation).

## Risks
A driver or model combination that loads but misbehaves on one GPU; a DirectML package with no further releases.

## Mitigation
CPU fallback at session creation. Integration tests
([`GpuInferenceTests`](../../tests/VisionRedact.IntegrationTests/AI/GpuInferenceTests.cs)) run every enumerated GPU
against the model-zoo reference outputs (tolerance 2e-2 for fused GPU arithmetic) and through the factory for both
built-in models. The device applies to detectors requested after it changes and each (model, device) pair is its own
cached session, so switching back to CPU needs no restart. Execution-provider code is
confined to `OnnxDetectorFactory`, so moving to Windows ML or CUDA is one adapter change.

## When to reconsider
When Windows ML is mature for this deployment, or if the product standardizes on NVIDIA (CUDA/TensorRT). With
inference no longer the limit, the next lever is hardware video decode (D3D11VA/NVDEC).
