# Bobcat.CodeFirst.Samples

Issue #105's design-by-use project: real Marten and Wolverine integration tests rewritten as
**code-first Bobcat specifications** — `[Scenario]` methods on a `Specification` class, no
`.feature` file, no generator — so the authoring API grew only where a port demanded it. What the
ports taught is written up in [`docs/code-first-specs.md`](../../docs/code-first-specs.md); this
README maps each spec back to the test it came from.

## Running

```bash
docker compose up -d                       # the repo's Postgres on 5445, from the repo root
dotnet test src/Bobcat.CodeFirst.Samples   # as a Microsoft.Testing.Platform host
dotnet run --project src/Bobcat.CodeFirst.Samples -- report            # Bobcat's own step-by-step report
dotnet run --project src/Bobcat.CodeFirst.Samples -- report --feature "Order saga"
```

It is also collected by `dotnet test` at the solution root, which is how CI runs it. Off CI with
no Postgres reachable it registers no scenarios, says so on stderr, and exits green (the csproj
ignores the platform's zero-tests exit code); on CI a missing database fails the build — the
same rule as `Bobcat.Marten.Tests`' `[PostgresFact]`.

Two hosts are registered once for the suite and reset between scenarios (`Hosts.cs`): `app`
(Wolverine + Marten, `IntegrateWithWolverine`, durable local queues — every Wolverine port ran
against a host shaped like this) and `projections` (Marten only, async daemon, conjoined tenancy).

## The ports

Originals are in the local clones at `~/code/wolverine` and `~/code/marten` at the time of
writing (2026-08-21); paths are relative to each repo's `src/`.

| Spec | Original | Shape |
|---|---|---|
| `AggregateHandlerWorkflowSpecs` · *Events then response* | wolverine `Persistence/MartenTests/AggregateHandlerWorkflow/aggregate_handler_workflow.cs:87` `events_then_response_invoke_no_return` (and `:134`, the with-return twin) | event-sourced aggregate command |
| `AggregateHandlerWorkflowSpecs` · *Response then events* | same file `:151` `response_then_events_invoke_no_return` | — |
| `AggregateHandlerWorkflowSpecs` · *A mix of events, messages and a response* | same file `:185` `return_mix_of_events_messages_and_response` | cascaded messages |
| `AggregateHandlerWorkflowSpecs` · *A Validate method can see the aggregate and stop the handler* | same file `:287` `using_the_aggregate_in_a_before_method` | two streams, one command each |
| `MartenOutboxSpecs` · *A document and an outgoing message commit in one transaction* | wolverine `Persistence/MartenTests/MartenOutbox_end_to_end.cs:39` `persist_and_send_message_one_tx` | outbox / durable local queue |
| `AsyncProjectionSpecs` · *A snapshot is built per tenant by the async daemon* | marten `DaemonTests/Aggregations/build_aggregate_projection.cs:30` `simple_scenario` | projection wait, set verification |
| `OrderSagaSpecs` · *Starting an order* | wolverine `Persistence/MartenTests/Saga/OrderSagaTests.cs:15` `When_starting_an_order` (`should_exist`, `should_not_be_completed`) over `Samples/OrderSagaSample/OrderSaga.cs` | Marten-persisted saga, tracked session |
| `OrderSagaSpecs` · *Completing an order*, *An order times out* | not in the original suite — the two flows the saga sample documents but its test stops short of | scheduled message played forward |

Domain types are copied from the originals (MIT, JasperFx) rather than referenced: the tests
own their domain, and the point is to port the tests.

## What each port demanded of the API

- **Aggregate workflow** — a typed vocabulary: `GivenStream`, `WhenCommand`, `ThenNewEvents`,
  `ThenMessageSent`. It lives in `CritterStackSpecification.cs` here, marked *to be replaced by
  `CritterStackFixture`* (issue #104), and is built entirely from the core API: `Captured<T>`
  for "the command's outcome, read by later steps", `WithRows` so the command and the events
  render as tables, the raw `Step(...)` escape hatch for the ordered event comparison.
  `Captured<T>.WithRows` exists because this port wanted to describe a `When` that also
  produces a value.
- **Outbox** — the scenario's own DI scope (`ctx.ScenarioServices("app")`) for the scoped
  `IMartenOutbox` + `IDocumentSession`, and a lesson: a step body returning a `Task` is awaited,
  so a task you want to *hold* (the handler's waiter) goes in a field.
- **Projections** — `ThenRows(...).KeyedBy("Id").ShouldMatch(new { Id = "one", A = 1, B = 1 }, …)`:
  the Storyteller-style set verification builder, added because this port's assertions were a
  table already. Also `WithRows(seeds)` over a `record StreamSeed(Tenant, Stream, params object[] Events)`,
  which is what made "records are self-describing" concrete — the events column renders as
  `MTAEvent, MTBEvent`.
- **Saga** — `Then(text, ctx => load(...)).ShouldBeNull()` / `.ShouldNotBeNull()`, and
  `When(..., () => started.Value.PlayScheduledMessagesAsync(...))` reading a `Given`'s capture —
  the value-handle model carrying across step kinds.
