# VisionRedact.Infrastructure.Persistence

`.vrproject` persistence. Dependencies: VisionRedact.Application only (System.Text.Json is part of the BCL). AnyCPU,
no native code.

| Type | Role |
|---|---|
| `JsonProjectRepository` | `IProjectRepository`: maps `ProjectDocument` to private DTOs and back; `schemaVersion` 1 (newer files rejected with `UnsupportedVersion`); enums written as names; streams to/from a `FileStream`; atomic save (write `<path>.tmp`, flush, `File.Move` over the target); boxes re-clamped on load through `NormalizedRect`; failures translated into `ProjectException` (`NotFound`, `AccessDenied`, `Corrupt`, `UnsupportedVersion`) |

Rules: DTOs, not domain types, on disk — the file format is a contract that must survive domain refactoring; no WPF,
no Prism, no other Infrastructure project. Relinking moved media and the source fingerprint check live in
`ProjectService` (Application).

Tests: `tests/VisionRedact.IntegrationTests/ProjectPersistenceTests.cs` (round trip, 50,000 detections, corrupt /
missing / future files, clamping, relink).
