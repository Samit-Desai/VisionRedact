# VisionRedact Studio

A desktop app that finds faces in video with AI and hides them before the video is shared. Built with
**C# / .NET 10 / WPF / Prism / Emgu CV (native OpenCV) / ONNX Runtime + DirectML / FFmpeg**.

**Architecture tour, 7 min:** [the video](https://samit-desai.github.io/visionredact-series/#episode-5) walks through
the threads, the three pipelines and the design patterns, each with the problem it solves.

## What video redaction is for

Before a video is shared, the people in it who never agreed to appear often have to be hidden:

- **Courts and legal cases**: evidence shared with the other side or shown in public.
- **Police and body cameras**: footage released to the public.
- **CCTV and security**: someone asks for footage of themselves (in the UK and EU, a subject access request under the
  GDPR), and identifiable third parties in it often need to be obscured, depending on the circumstances. The UK
  ICO's CCTV guidance names blurring, masking and a solid fill.
- **Schools, hospitals and workplaces**: recordings of children, patients or staff.
- **News and research**: bystanders who never agreed to appear.

By hand, that means drawing over every face in every frame, and my 23-minute test recording (59.94 fps) has 82,373
frames. This app uses AI to find the faces on the GPU, on the PC itself, so the footage never leaves the machine.
Every face it finds stays blurred until a person rejects it. The reviewer draws over anything it missed, and export
writes a new file and leaves the original untouched.

It is a personal project: a small, original application, not a clone of any commercial product.

## Status

| Slice | Scope | Status |
|---|---|---|
| 1 | Solution architecture, Prism shell, open video (Emgu CV), metadata, play / pause / seek / step, timeline | **Done** |
| 2 | Custom `VideoTimelineControl`, layered `VideoSurface`, decode-ahead preview buffer, diagnostics panel | **Done** |
| 3 | ONNX detectors on the current frame (UltraFace, YOLO), coordinate mapping, overlay | **Done** |
| 4 | Background full-video analysis: bounded Channels, sampling, progress, cancellation, virtualized detection list | **Done** |
| 5 | Manual redaction regions, blur / pixelate / black box, human review (approve / reject per track) | **Done** |
| 6 | Export, project persistence, profiling, memory soak test | **Done** |
| 7 | Identity tracking (IoU + prediction), visual region tracking (KCF), GPU inference via DirectML | **Done** |

**346 tests passing** (Domain 76, Application 182, Integration 88). Measured numbers are in
[Performance.md](docs/Performance.md); nothing is claimed there without a measurement.

## Features

- **Playback** of large files (tested on a 4.3 GB 1080p59.94 AVCHD `.mts`): decode task + present task over a
  bounded decode-ahead channel (3 frames), anchored clock, late-frame dropping, coalesced seeks, frame stepping.
  Measured: 59.9 decode FPS, 61.9 presented FPS, 0 late drops, 4 live frames.
- **AI detection** with ONNX Runtime: UltraFace RFB-320 (fast) and YOLO11n-face (finds small faces); any Ultralytics
  YOLO `detect` export dropped into `models/` is picked up, with classes read from its `names` metadata. Several models
  can run together.
- **GPU inference through DirectML** on any DirectX 12 GPU (NVIDIA, AMD, Intel), GPUs enumerated through DXGI, and the CPU
  when a GPU can't load the model. YOLO11n p50: CPU 23.4 ms, Intel Arc 14.2 ms, RTX 3500 4.7 ms.
- **Sampling** (every frame / every Nth / target rate) with **track interpolation** between analyzed frames.
- **Identity tracking**: `IouObjectTracker` — IoU on a constant-velocity predicted box, plus a size-gated
  center-distance fallback for small fast faces.
- **Visual region tracking**: "track this region" follows a manual box with OpenCV KCF (`RegionTrackingJob`).
- **Human review**: virtualized detection list, approve / reject a whole track, timeline markers, "detect this frame"
  (`D`).
- **Redaction effects**: Gaussian blur (reduced resolution), pixelate, solid black — the same planner drives preview
  and export.
- **Export**: separate pipeline (decode ‖ redact ‖ encode, in order, every frame), temp file + frame-count check +
  rename after the count check; the source is never modified. With FFmpeg installed, MP4 export streams frames to `ffmpeg.exe`, which
  encodes H.264 on the first working encoder (Media Foundation — Microsoft's software encoder, measured faster than
  NVENC inside the pipeline — then NVENC, Quick Sync, AMF, OpenH264) and keeps the source's audio (AAC):
  147–167 frames/s on the 1080p MTS, files under a third of the size at the chosen bitrate (picture quality not measured). Without FFmpeg it falls back to OpenCV's writer (72–80
  frames/s H.264, video only). See [ExportPipeline.md](docs/ExportPipeline.md).
- **Projects**: `.vrproject` JSON (versioned schema, atomic save), relink of moved media with a source fingerprint check.
- **Diagnostics panel**: decode/present FPS, buffer and channel occupancy, AI FPS, execution provider, live native
  frames, private bytes, GC counts.

## Quick start

Requirements: Windows 10/11 x64, .NET SDK 10. Target frameworks: `net10.0` (libraries) and
`net10.0-windows10.0.20348.0` (WPF app).

### Models

Model files are binary artefacts and are not committed.

```powershell
./scripts/download-model.ps1
```

downloads UltraFace RFB-320 (ONNX Model Zoo, MIT) into `models/`, plus the model zoo's reference input/outputs used
by the conformance tests; every file is checked against a SHA-256 hash. `-SkipTestData` skips the test data.

The script also downloads the YOLO11n face model as `models/yolo11n-face.onnx` (Hugging Face
`AdamCodd/YOLOv11n-face-detection`, trained on WIDER FACE; named in `OnnxModelCatalog`). Its embedded license is
AGPL-3.0 — fine for evaluation; commercial distribution would need an Ultralytics license. Any other Ultralytics
`detect` ONNX export (static input size) can be copied into `models/` the same way.

Optional, for H.264 MP4 export: `./scripts/download-openh264.ps1` fetches Cisco's `openh264-1.8.0-win64.dll`
(~380 KB, Authenticode signature checked) into `native/` (git-ignored; Cisco's license requires each user to download
it from Cisco). Without it, MP4 export uses MPEG-4 Part 2 (`mp4v`).

Optional, for faster MP4 export with audio: `./scripts/download-ffmpeg.ps1` fetches FFmpeg 9.0.2 (BtbN
`win64-lgpl-shared` build, 73 MB download, SHA-256 checked) into `ffmpeg/` (git-ignored); the build copies it to
`<output>\ffmpeg\`. Without it, export uses the OpenCV writer above; the integration tests that need FFmpeg are skipped.

Without models the app still runs (playback, manual redaction, export); tests that need a model are skipped with a
message naming the missing file.

### Build, test, run

```bash
dotnet build VisionRedact.slnx
```

```bash
dotnet test VisionRedact.slnx
```

```bash
dotnet run --project src/VisionRedact.Presentation.Wpf
```

You can also pass a file: `VisionRedactStudio.exe C:\videos\clip.mp4` (or a `.vrproject`).

Benchmarks (Release): `dotnet run -c Release --project tools/VisionRedact.Benchmarks -- "D:\path\to\video.mts"`
(B1–B7; add `--tracking` for B8, `--scenes` for B9, `--export` for export throughput only). Public demo video: `./scripts/download-demo-video.ps1` (Tears of Steel, CC BY 3.0, 557 MB) into `media/`.

Keyboard: `Ctrl+O` open video, `Ctrl+Shift+O` open project, `Ctrl+S` save project, `Ctrl+E` export,
`Space` play/pause, `←/→` step one frame, `Home/End` jump to start/end, `D` detect on this frame,
`Delete` remove the selected region.

Logs (technical details, native error text, thread ids): `%LOCALAPPDATA%\VisionRedact\logs`.

Debugging note: under the Visual Studio debugger, turn off *Tools > Options > Debugging > General > Enable UI
Debugging Tools for XAML* (see [ErrorHandling.md](docs/ErrorHandling.md#visual-studio-xaml-tools-and-a-second-dispatcher)).
`Ctrl+F5` is unaffected.

## Solution map

```text
src/
  VisionRedact.Domain                    pure model: VideoMetadata, NormalizedRect, LetterboxTransform, Detection,
                                         DetectionTrack, RedactionRegion, ProcessingJobState/JobStateMachine — no packages
  VisionRedact.Application               ports + use cases: IVideoDecoder, OwnedVideoFrame, PlaybackSession, VideoWorkspace,
                                         AnalysisJob, DetectorHost, IDetectionRepository, IouObjectTracker, RegionTrackingJob,
                                         RedactionPlanner, VideoExportService, ProjectService, ProcessingJobManager
  VisionRedact.Infrastructure.Video      Emgu CV: EmguVideoDecoder, MatVideoFrame, EmguRuntimeProbe, redaction effects,
                                         EmguVideoWriter, EmguVisualTracker; FfmpegVideoWriter (ffmpeg.exe process)
  VisionRedact.Infrastructure.AI         ONNX Runtime (+ DirectML): OnnxDetectorFactory, OnnxObjectDetector,
                                         LetterboxPreprocessor, output decoders, OnnxModelCatalog, DxgiAdapterCatalog
  VisionRedact.Infrastructure.Persistence  JsonProjectRepository (.vrproject, System.Text.Json)
  VisionRedact.Presentation.Wpf          Prism/DryIoc shell, feature views/view models, custom controls,
                                         WriteableBitmap presenter
tests/
  VisionRedact.Domain.Tests              geometry, letterbox mapping, frame/time arithmetic, job state machine
  VisionRedact.Application.Tests         playback, analysis, jobs, repository, tracking, redaction, export — fakes only,
                                         no WPF/native
  VisionRedact.IntegrationTests          real OpenCV and ONNX Runtime (CPU + every GPU) on synthetic videos generated at
                                         test time; persistence; soak
tools/
  VisionRedact.Benchmarks                B1–B8 measurement harness behind docs/Performance.md
scripts/
  download-model.ps1                     UltraFace + model-zoo reference data, SHA-256 verified
  download-openh264.ps1                  Cisco OpenH264 DLL for the OpenCV writer, signature checked
  download-ffmpeg.ps1                    FFmpeg 9.0.2 LGPL shared build for the FFmpeg writer, SHA-256 verified
```

Each `src/` project has a short README with its key types and rules.

## Documentation

| Document | What it covers |
|---|---|
| **[Walkthrough](docs/Walkthrough.md)** | **Start here if you're new to video or AI: every design and optimization explained in plain English, with diagrams and a glossary** |
| [Architecture](docs/Architecture.md) | Layers, dependency rules, main interfaces, Prism composition |
| [SystemDesign](docs/SystemDesign.md) | Playback / analysis / export pipelines, and why they are separate |
| [ThreadingModel](docs/ThreadingModel.md) | Which thread does what; Dispatcher boundary; async rules |
| [MemoryManagement](docs/MemoryManagement.md) | Frame ownership, ONNX resources, bounded memory for 20 GB files |
| [ManagedNativeInterop](docs/ManagedNativeInterop.md) | C# → Emgu → cvextern.dll → OpenCV; ONNX Runtime → DirectML; DXGI COM; ffmpeg.exe process; failure modes |
| [VideoPipeline](docs/VideoPipeline.md) | Decode-ahead playback, clock, frame dropping, seeking |
| [AiPipeline](docs/AiPipeline.md) | Models, preprocessing, sampling, GPU inference, analysis pipeline |
| [ExportPipeline](docs/ExportPipeline.md) | Snapshot, pipelined export, FFmpeg and OpenCV writers, codecs, audio, correctness guarantees |
| [ErrorHandling](docs/ErrorHandling.md) | Exception translation, fault states, job failures, global handlers |
| [TestingStrategy](docs/TestingStrategy.md) | What is tested where, and why |
| [Performance](docs/Performance.md) | Measured numbers and the five optimizations (before/after) |
| [Profiling](docs/Profiling.md) | Which tool answers which performance question |
| [Tradeoffs](docs/Tradeoffs.md) | Decisions and what they cost |
| [EngineeringGuidelines](docs/EngineeringGuidelines.md) | Team rules + a worked code-review example |
| [ADRs](docs/adr/README.md) | Architecture decision records |

## Licence

VisionRedact Studio is licensed under the GNU General Public License v3.0 ([LICENSE](LICENSE)), because it uses
Emgu CV under Emgu's GPL v3 option (Emgu CV is dual-licensed: GPL v3 or a paid commercial license). A closed-source
product would need an Emgu commercial license or a different OpenCV binding.

No third-party binary, model or video is committed: scripts download them, each under its own terms. In short:
UltraFace and YuNet are MIT; the YOLO face models are Ultralytics-trained, which Ultralytics licenses under AGPL-3.0
(the YOLO11n model card says Apache-2.0; its embedded metadata says AGPL-3.0); the ArcFace face-embedding model is
for non-commercial research only; WIDER FACE, the detectors' training data, is CC BY-NC-ND; the optional FFmpeg
build is the LGPL variant, run unmodified as a separate process. Full list: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
