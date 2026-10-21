# Architecture Decision Records

Format: Context · Options · Decision · Why · Benefits · Costs · Risks · Mitigation · When to reconsider.

| # | Decision | Status |
|---|---|---|
| [0001](0001-pragmatic-clean-architecture.md) | Pragmatic Clean Architecture | Accepted (slice 1) |
| [0002](0002-emgu-cv-for-opencv.md) | Emgu CV as the OpenCV integration layer | Accepted (slice 1) |
| [0003](0003-prism-dryioc.md) | Prism 8 + DryIoc for WPF composition | Accepted (slice 1) |
| [0004](0004-onnx-runtime-multi-model.md) | ONNX Runtime for inference, pluggable models | Accepted (slice 3) |
| [0005](0005-bounded-channels.md) | Bounded `System.Threading.Channels` between pipeline stages | Accepted (design; implemented slice 4) |
| [0006](0006-separate-playback-and-analysis.md) | Separate playback and analysis pipelines | Accepted (slice 1) |
| [0007](0007-separate-export-pipeline.md) | Export as an independent pipeline | Accepted (design; implemented slice 6) |
| [0008](0008-native-frame-ownership.md) | Explicit native frame ownership (`OwnedVideoFrame`) | Accepted (slice 1) |
| [0009](0009-preview-frame-dropping.md) | Preview may drop stale frames; export never does | Accepted (slice 1) |
| [0010](0010-eventaggregator-coarse-events-only.md) | EventAggregator for coarse events only | Accepted (slice 1) |
| [0011](0011-wpf-rendering-writeablebitmap.md) | WPF rendering via one WriteableBitmap + coalesced dispatch | Accepted (slice 1) |
| [0012](0012-normalized-coordinates.md) | Normalized coordinates in the domain | Accepted (slice 3) |
| [0013](0013-frame-sampling.md) | Frame sampling strategies, track interpolation between samples | Accepted (slice 4; interpolation 2026-09) |
| [0014](0014-human-review-fail-safe.md) | Human review: AI results are proposals, pending is redacted | Accepted (slice 5) |
| [0015](0015-redaction-effects-strategy.md) | Redaction effects as strategies, one shared planner | Accepted (slice 5) |
| [0016](0016-detection-vs-track.md) | Detection vs DetectionTrack | Accepted (slice 7) |
| [0017](0017-directml-gpu-inference.md) | GPU inference through the DirectML execution provider | Accepted |
| [0018](0018-opencv-only-media-mvp.md) | OpenCV-only media stack for the MVP (limitations) | Accepted (slice 1); export part superseded by 0020 |
| [0019](0019-export-snapshot-atomic-output.md) | Export from a snapshot, write via a temporary file | Accepted (slice 6) |
| [0020](0020-ffmpeg-exporter.md) | FFmpeg exporter as an external process (H.264 via Media Foundation, audio) | Accepted (2026-09) |
| [0021](0021-model-format-profiles-nms-free-yolo.md) | Model format profiles, NMS as a decorator, NMS-free YOLO (YOLO26) | Accepted (2026-10) |
| [0022](0022-person-identity.md) | Person identity: group face tracks into people (privacy by design, fail-safe keep) | Accepted (2026-10) |
| [0023](0023-timeline-hover-preview.md) | Timeline hover preview: latest request wins on a private decoder | Accepted (2026-10) |
| [0024](0024-unique-track-ids.md) | Track ids unique across analysis runs | Accepted (2026-10) |

ADRs are written when the decision is actually made and exercised, so their reasoning is grounded in the code.
