# Third-party notices

VisionRedact Studio is licensed under the GNU General Public License v3.0 (see [LICENSE](LICENSE)). It uses the
components below. Licences were read from each package's metadata, the model cards and the download scripts on
2026-10-11. This is a summary, not legal advice; each component's own licence text governs.

## NuGet packages (restored at build time)

| Package | Version | Licence |
|---|---|---|
| Emgu.CV, Emgu.CV.runtime.windows | 4.13.0.5924 | Dual: GPL v3 or Emgu commercial licence (used here under GPL v3). Includes OpenCV, Apache 2.0 |
| OpenCV's FFmpeg wrapper `opencv_videoio_ffmpeg` (shipped in Emgu.CV.runtime.windows) | 4.13 | LGPL (an FFmpeg build) |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | MIT |
| Microsoft.AI.DirectML (dependency of the above) | 1.15.4 | Microsoft Software License Terms (DirectML) |
| Prism.DryIoc, Prism.Wpf, Prism.Core | 8.1.97 | MIT |
| DryIoc.dll | 4.7.7 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12 | MIT |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.3.1, 10.0.0, 7.0.0 | Apache 2.0 |
| xunit, xunit.runner.visualstudio (tests only) | 2.9.3, 3.1.5 | Apache 2.0 |
| Microsoft.NET.Test.Sdk, Microsoft.Extensions.TimeProvider.Testing (tests only) | 18.10.1, 10.10.0 | MIT |

## Downloaded by scripts (never committed)

| Component | Script | Licence |
|---|---|---|
| FFmpeg 9.0, LGPL shared build (BtbN/FFmpeg-Builds) | `scripts/download-ffmpeg.ps1` | LGPL; run unmodified as a separate process |
| OpenH264 1.8.0 (Cisco binary) | `scripts/download-openh264.ps1` | Cisco's binary licence; each user downloads it from Cisco |
| UltraFace version-RFB-320 (ONNX Model Zoo) | `scripts/download-model.ps1` | MIT |
| YOLO11n face (Hugging Face AdamCodd/YOLOv11n-face-detection) | `scripts/download-model.ps1` | Model card: Apache-2.0. Ultralytics-trained: Ultralytics states such models fall under AGPL-3.0, and the exported file's metadata says AGPL-3.0 |
| YOLO26l face (Hugging Face uralman/yolo26l-widerface) | `scripts/download-model.ps1` | AGPL-3.0 |
| YuNet face_detection_yunet_2023mar (OpenCV Zoo) | `scripts/download-face-models.ps1` | MIT |
| ArcFace w600k_mbf (InsightFace buffalo_s, MobileFaceNet) | `scripts/download-face-models.ps1` | Non-commercial research only (InsightFace) |
| WIDER FACE (training data of the face detectors; not downloaded) | — | CC BY-NC-ND (the dataset's site states no version) |

## Demo footage (downloaded or shown in the docs and videos; never committed)

| Footage | Licence and credit |
|---|---|
| Tears of Steel | CC BY 3.0 (creativecommons.org/licenses/by/3.0). Credit: (CC) Blender Foundation \| mango.blender.org. Where shown with redaction applied, faces are blurred by VisionRedact; Blender and Mango logos not shown |
| Pexels clips by Talha Uguz, Towfiqu barbhuiya, William Fortunato | Pexels License: free to use; no endorsement implied; no one shown in a bad light; not sold unaltered |

A commercial product built from this code would need an Emgu CV commercial licence (or another OpenCV binding), a
permissively licensed face-embedding model (for example OpenCV Zoo SFace, Apache-2.0; check its training-data terms)
and detection models whose weights and training data allow commercial use.
