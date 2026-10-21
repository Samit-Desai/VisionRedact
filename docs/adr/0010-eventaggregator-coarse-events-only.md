# ADR-0010: EventAggregator for Coarse Events Only

## Context
Feature ViewModels need to react to application events without referencing each other. Video produces 30–60 frames
per second.

## Options
1. Publish every frame / position change through `IEventAggregator`.
2. Use EventAggregator for coarse lifecycle events; dedicated paths for high-frequency data.

## Decision
Option 2. Events: `VideoOpenedEvent` (later ProjectOpened, DetectionCompleted, ExportCompleted,
SelectedDetectionChanged). Frames go through `IPreviewFrameSink`; position rides on the presenter's render callback;
analysis data flows through Channels and the detection repository.

## Why
Each publish allocates, walks subscriber lists and, with `ThreadOption.UIThread`, posts a Dispatcher operation.
At frame rate that is a self-inflicted Dispatcher flood with no backpressure and no ability to drop stale items.
It also hides the data flow: nothing in the type system says who consumes frames or who disposes them.

## Benefits
Loose coupling where it matters; high-frequency paths stay explicit, bounded and measurable.

## Costs
Two communication mechanisms to learn.

## Risks
"Just one more event" drift toward high-frequency use.

## Mitigation
Engineering guideline + review checklist; events carry references to sessions/results, not per-frame data.

## When to reconsider
Never for frame data. For medium-frequency UI updates (e.g. progress), throttle before publishing.
