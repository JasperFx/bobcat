# Event Modeling and Spec Driven Development

The Event Modeling workflow across the Critter Stack -- declaring the model in C#, turning the board's
examples into executable specifications, scaffolding handlers for the slices that have no code yet,
and catching drift between the design and the code -- is written down end to end in **one** place:

**[Event Modeling with the Critter Stack](https://wolverinefx.net/tutorials/event-modeling.html)**, in
the Wolverine documentation.

<!-- JEREMY -->
Bobcat used to keep a second narrative here. One is better: the workflow crosses Wolverine, Marten,
JasperFx and Bobcat, and two copies of it would drift the way the tutorial warns a board and its code
drift.

## Bobcat's part, and where it is documented

| | |
|---|---|
| Importing an eventmodelers.ai board as C# stubs, an `EventModelDefinition`, and optionally a specification per example | [`bobcat import-event-model`](../bobcat-tool.md#import-event-model) |
| Binding a specification to its slice -- `[BobcatSpec]`, `[BobcatSlice]`, `TriggeredBy`, `Pending = true` | [Writing Specifications](../marker-steps.md#bobcatslice) |
| The slice's command, aggregates, events, read models and messages, read off the vocabulary's `[EventModelRole]` attributes | [Binding, not roles](../marker-steps.md#event-model-roles) |
| The Given/When/Then vocabulary the specifications are written in | [Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat) (Wolverine docs) |
| What a specification claims about an event, an aggregate or a read model -- partial objects, collection expectations, tolerances, member checks and the C# version they need | [Expected Objects](../expected-objects.md) |
| The gate that keeps the model and the specifications honest (`SpecIdentityAudit`, in WolverineFx.Bobcat) | [Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat) (Wolverine docs) |
| How the vocabulary maps onto the model -- the act's command, `GivenContext` as `ReadsFrom`, a read act (`WhenReading`) making a **Query** slice | [The Event Model, from the vocabulary](https://wolverinefx.net/guide/testing-with-bobcat#the-event-model-from-the-vocabulary) (Wolverine docs) |
| Seeing the model, which slices have specifications, and how those specifications last ran | [The Bobcat UI](../bobcat-ui.md#the-event-model-canvas) and [`bobcat slice-coverage`](../agent-queries.md) |
| Running one slice's specifications, a chapter's, or every `View` | [Selecting by Event Model coordinates](../bobcat-tool.md#selecting-by-coordinates) |

Skeleton generation for declared-only slices is Wolverine's `scaffold` command, not Bobcat's.

For a coding agent working the same loop -- import a board, write the specs, scaffold, implement until
green -- the [`critterstack-sdd-*` AI skills](https://ai-skills.jasperfx.net) carry the whole workflow,
including the import's callouts and how to read a red spec.
