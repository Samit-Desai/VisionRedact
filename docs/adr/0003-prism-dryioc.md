# ADR-0003: Prism 8 + DryIoc for WPF Composition

## Context
The shell hosts several independent feature areas (player, inspector, later detections, diagnostics, export) that
must be composed, communicate coarsely, and be testable.

## Options
1. Plain WPF + Microsoft.Extensions.DependencyInjection + hand-rolled MVVM.
2. CommunityToolkit.Mvvm.
3. Prism 9 (dual licence: community / commercial).
4. Prism 8.1.97 (MIT) with DryIoc.

## Decision
Prism 8.1.97 with `Prism.DryIoc`.

## Why
Prism supplies exactly the composition pieces we need — regions, EventAggregator, dialog service, view-model location,
DI integration — with an API used widely in large WPF codebases. 8.1.97 is MIT-licensed with no licence-key
registration; the Prism 9 API differences relevant here are small (namespaces for dialogs, `AsyncDelegateCommand`),
so migrating later is cheap. DryIoc is fast and supports open generics (`ILogger<>`).

## Benefits
Feature isolation via regions; coarse pub/sub; testable dialogs; explicit view→VM mapping.

## Costs
Prism 8 lacks an async command, so we wrote `AsyncCommand`; Prism is a large dependency for a small app.

## Risks
Misusing EventAggregator for high-frequency data (see ADR-0010); service-locator creep via `ContainerLocator`.

## Mitigation
Constructor injection only; `ViewModelLocationProvider.Register` for explicit mapping; ADR-0010 rules.

## When to reconsider
If the project moves to Prism 9 features (e.g. `AsyncDelegateCommand`, new dialog API) or needs commercial support.
