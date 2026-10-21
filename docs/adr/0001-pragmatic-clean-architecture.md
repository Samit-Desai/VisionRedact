# ADR-0001: Pragmatic Clean Architecture

## Context
The application combines fast-moving native/AI technology (Emgu CV, ONNX Runtime) with WPF and Prism. The rules that
matter most — frame ownership, timing, cancellation, redaction decisions — must be testable without a UI or native
DLLs, and each technology must be replaceable without rewriting the rest (the same property that makes a legacy
codebase modernizable incrementally).

## Options
1. Single WPF project, folders by type (Views/ViewModels/Services).
2. Full Clean Architecture: interface per class, mediator, CQRS, mapping layers.
3. Pragmatic layering: Domain / Application (ports) / Infrastructure adapters / Presentation, interfaces only where a
   second implementation or test double is needed.

## Decision
Option 3. Dependencies point inward; Application defines ports; Presentation is the composition root.

## Why
Option 1 makes the playback worker untestable without WPF and lets Emgu types leak everywhere. Option 2 adds
ceremony (mediators, DTO mapping) that buys nothing at this size and slows every change. Option 3 gives the test
boundary we need — `Application.Tests` has no WPF/native references yet tests timing, ownership and faults — with
five projects, not fifteen (six today: Infrastructure.Persistence was added later).

## Benefits
Fast deterministic tests; technology swap is one project; boundaries set by project references and checked by a test
(`DependencyRulesTests`: Domain and Application may not reference OpenCV, ONNX Runtime, WPF, Prism or an outer
project, in their compiled references or their project files), not by convention alone.

## Costs
More projects and some indirection; frame data must cross the boundary as `IntPtr`/span rather than `Mat`.

## Risks
Abstractions designed before a second implementation exists can be wrong ("leaky ports").

## Mitigation
Every interface names its reason (Architecture.md table); rejected interfaces are documented; ports are shaped by
real usage (e.g. `IVideoDecoder` is synchronous because decoding is).

## When to reconsider
If Application starts needing Emgu types to do its job (e.g. AI preprocessing in managed code proves too slow), move
that step into an adapter rather than weakening the rule.
