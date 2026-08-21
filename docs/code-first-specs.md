# Code-first specifications

Issue #105. The code-first authoring origin sketched in `src/Bobcat/notes.md` (`[Spec]` /
`[FormatAs]`, never built), resolved by **use rather than up front**: Jeremy's decision of record
(2026-08-21) was to take genuinely slow Marten and Wolverine integration tests, rewrite them as
code-first Bobcat specs, and grow the API only as those ports demanded. This is what that produced
and what it taught. The ports themselves are `src/Bobcat.CodeFirst.Samples/` (its README maps each
spec to the original test, file and line).

## The API, as it stands

Everything lives in `src/Bobcat/CodeFirst/`, additive to core; nothing else in `Bobcat` changed.

```csharp
public class OrderSagaSpecs : Specification              // feature title: "Order Saga"
{
    [Scenario("Starting an order")]                      // one [Scenario] method = one scenario
    public void starting_an_order()
    {
        var orderId = Guid.NewGuid().ToString();

        var run = When("StartOrder is received", ctx => ctx.InvokeMessageAndWaitAsync(new StartOrder(orderId), "app"))
            .WithRows(new StartOrder(orderId));          // records are self-describing → input table

        Then("the Order saga document", ctx => load(ctx, orderId)).ShouldNotBeNull();
        Then("the OrderTimeout scheduled by the saga", () => run.Value.Scheduled.SingleMessage<OrderTimeout>().Id).ShouldBe(orderId);
        ThenRows("the open orders", ctx => query(ctx)).KeyedBy("Id").ShouldMatch(new { Id = orderId, Completed = false });
    }
}
```

```csharp
runner.AddSpecification<OrderSagaSpecs>();               // or ScanForSpecifications(assembly), beside ScanForFeatures
```

- **`Specification : Fixture`.** A specification *is* the fixture: one fresh instance per
  scenario, the same `Context`, the same recovery-hint attributes. `SpecificationFeature.Build(type)`
  reflects once per type at registration (the `[Scenario]` methods, hooks by the Gherkin naming
  convention — `BeforeEach`/`AfterEach`, static `BeforeAll`/`AfterAll`, `Async` suffix, the
  attributes as overrides) and produces the same `FeatureDefinition` / `DelegateExecutionStep` model
  the generator emits. `FixtureType` is the specification itself, so `BobcatRunner.AddFeature`'s
  hint scoping works unchanged.
- **Compose, then execute.** A scenario method is invoked at plan-build time and *declares* steps;
  the engine then executes them with the timeout, continuation rules and observers every other
  scenario gets. Two consequences the ports kept bumping into: the method body cannot `await` a
  step's outcome — a `Given`/`When` that returns a value hands back a `Captured<T>`, read as
  `.Value` inside a later step — and anything needing the step context belongs in a step body. A
  `Captured` read too early throws with an explanation; an exception escaping the scenario method
  itself becomes a single failing "composing the scenario" step rather than taking the run down.
- **Overloads, not a DSL.** `Given`/`When` take `Action`, `Func<Task>`, `Func<IStepContext, Task>`,
  and value-returning `Func<T>` / `Func<Task<T>>` / `Func<IStepContext, Task<T>>` (→ `Captured<T>`).
  `Then` has the same shapes, plus `Then(text, () => value)` returning a `ValueExpectation<T>` —
  `ShouldBe`, `ShouldNotBe`, `ShouldBeNull`, `ShouldNotBeNull`, `ShouldSatisfy(predicate, "be positive")`,
  `ShouldMatch("NULL")` through the Gherkin cell checker — and `Then(() => account.Balance)` with
  `[CallerArgumentExpression]` for text-free asserts ("account.Balance should be 50"). `Check(text, bool)`
  is the boolean step. `ThenRows(text, () => rows).KeyedBy(...).ShouldMatch(...)` is set
  verification through the same `SetVerificationComparer` a Gherkin `[SetVerification]` uses.
  `Step(kind, text, (ctx, result, ct) => …)` is the raw escape hatch.
- **Tables from records.** `.WithRows(objects)` on any step (and on a `Captured<T>`) renders the
  objects' public properties as the step's input table; a marker record with no properties, or rows
  of mixed types, gets a `type` column — so an event stream reads as a list of event names.
  `RowTable` is the describer; `SetExpectation` reuses it to flatten expected rows.
- **The one deliberate divergence from Gherkin: a `Then` body that throws is an assertion failure,
  not a crash.** The step is marked failed with the exception's message as a cell, and the scenario
  continues, so a run with three wrong assertions reports three, not one. That is the
  `ProjectionScenario` contract (action failures stop, assertion failures accumulate) and what
  anyone using Shouldly inside a `Then` wants. `Given`/`When` that throw are critical exactly as in
  Gherkin, and `SpecCriticalException`/`SpecCatastrophicException` mean what they mean anywhere. The
  generator still treats a throwing `[Then]` *method* as critical; the two surfaces disagree here on
  purpose, and this paragraph is the record of it.
- **Discovery is reflection, once.** `[Scenario]` attribute → title from the attribute or the
  method name (`events_then_response` → "events then response"); `Tags` on the attribute use the
  Gherkin vocabulary (`retry(2)`, `isolated`, `timeout(60)`). Class name minus
  `Specification`/`Specs`/`Spec`/`Fixture` → feature title, `[FixtureTitle]` overrides. The hot path
  is lambdas — no reflection per step.

`Bobcat.Mtp.SampleHost` and `Bobcat.Supervisor.SampleWorker` — the two in-repo hand-rolled
`scenario(...)` helpers the issue named (the third had already been folded into unit tests) — are
now specifications. The sample worker uses the raw `Step` on purpose: its probes crash and throw to
exercise the supervisor, and must reach the platform as errors, not as gathered assertion failures.

## Acceptance: the twin renders the same

`src/Bobcat.Acceptance.Tests/CodeFirstTwinTests.cs` runs `Features/CodeFirstTwin.feature` through
the generator and `CodeFirstTwinSpecification` through `SpecificationFeature`, renders both to
`SpecRender`, and asserts the shapes are identical: feature and scenario title, step kinds and
texts, statuses, failure levels, comparison cells (name, status, expected, actual, note) and the
set-verification table (columns, rows, per-cell status). The one thing excluded is `StepId` — the
generator keys it on the matched method name, code-first on position, and nothing downstream keys
off it. It passed first time, which is the cleanest evidence that the compose-then-execute model
lands on the same runtime model rather than beside it.

## What the ports demanded

Four shapes were ported (see the sample README for file:line): an event-sourced aggregate command
workflow (Wolverine `aggregate_handler_workflow`), a Marten outbox commit (`MartenOutbox_end_to_end`),
an async projection across tenants (Marten `build_aggregate_projection.simple_scenario`), and a
Marten-persisted saga with a scheduled timeout (`OrderSagaTests` + the `OrderSagaSample` saga).

What they asked for, in the order they asked:

1. **A value handle across steps** (`Captured<T>`). The aggregate port's first line was "send the
   command, then assert on what came back", which in a compose-then-execute model needs something
   to carry the result from the When into the Thens. Every later port used it.
2. **`WithRows` on a value-producing step.** The command a `When` sends is worth seeing in the
   report; `When<T>` returns a `Captured<T>` rather than a `StepHandle`, so `Captured<T>` grew
   `WithRows` too.
3. **Set verification from code** (`ThenRows … KeyedBy … ShouldMatch`). The projection port's
   assertions were already a table — three ids, two counts each, per tenant — and writing them as
   six `ShouldBe`s would have been worse than the xUnit original. This is the one Storyteller-style
   fluent builder that earned its place; a data-setup builder did not come up, because
   `Given(...).WithRows(records)` already renders the data and the records are the setup.
4. **Self-describing records** (`RowTable`). `record StreamSeed(Tenant, Stream, params object[] Events)`
   renders as a row whose `Events` cell says `MTAEvent, MTBEvent` — the "records are
   self-describing" line in the issue, made concrete by a port that had six streams to declare.
5. **The Then-throws-is-a-failure rule.** Writing the aggregate port with `ShouldBe`-style
   expectations made the gathered-failures report obvious (see below); the moment a Then used a
   plain `throw`, Gherkin's critical-on-exception would have hidden the later assertions.
6. **`IStepContext`-taking overloads.** Every Wolverine/Marten step wants the context
   (`ctx.InvokeMessageAndWaitAsync`, `ctx.ScenarioServices`, `ctx.GetRootService<IDocumentStore>`);
   `Context!` from the fixture works but `ctx =>` reads better and cannot be used too early.
7. **Named host resources.** Two hosts with different store settings, so every helper takes the
   resource name — which `Bobcat.CritterStack` already supported.

What the ports did **not** ask for, and was therefore not built: a data-setup fluent builder; a
`ThenDocument<T>(id, assert)` helper (plain `Then(text, ctx => load(...)).ShouldNotBeNull()` was
enough); `[FormatAs]`-style step-text templates on fixture methods (the typed steps in
`CritterStackSpecification` build their own text, and it is clearer); a generator-free binding of
code-first steps back to `[Given]/[When]/[Then]` fixture methods (the notes.md origin — a
specification can *host* a fixture and call its methods inside step bodies, which covers the
sharing case without a second matching engine).

## Which shapes the API handles well, and which were awkward

**Well:**

- *Command → outcome → assertions.* `var run = WhenCommand<LetterAggregate>(new RaiseABC(id), id);
  ThenNewEvents(run, typeof(AEvent), …); Then("the aggregate's ACount", () => run.Value.Aggregate!.ACount).ShouldBe(1);`
  reads as the scenario it is, and the report shows the command row, the event table, and each
  count with expected/actual side by side.
- *Tables.* Both directions — input (`WithRows`) and verification (`ThenRows`) — render as the
  same per-cell table the Gherkin side gets, including missing and extra rows.
- *Waits.* `When("the async daemon has caught up …", ctx => ctx.WaitForNonStaleProjectionsAsync(…))`
  is one line and its duration shows on the step (521ms on the first run), which is exactly the
  "is this hung or slow?" information notes.md wanted.
- *Hosting once.* Two hosts shared by nine scenarios, reset between them: the whole suite runs
  in about 5.5s under `dotnet test`, host start-up included. The originals stood a host up per
  test class (xUnit `IAsyncLifetime`).

**Awkward:**

- *Value-returning step bodies are awaited.* `Given("a waiter", () => Handler.WaitForNextMessage())`
  infers `Func<Task<T>>` and awaits the waiter inside the Given — a 15s hang in the outbox port
  until the waiter went into a field. The overloads are the right default; the pitfall needs the
  line in the docs it now has.
- *Step text for `ShouldBe` chains.* "the order is completed should be false" read badly until
  the text became "whether the order is completed". Text-plus-suffix is the right model (it is
  what makes the twin render identically) but the author has to write the text as a noun phrase.
- *`ShouldSatisfy` on an object.* `ThenMessageSent<…, Response>(run).ShouldSatisfy(r => r.ACount == 1, "carry ACount 1")`
  renders its actual as the object's `ToString()`. A projector overload (`ShouldSatisfy(r => r.ACount, 1)`)
  would fix it; not built because one port wanted it once.
- *Two type arguments on the typed steps.* `ThenMessageSent<LetterAggregate, Response>(run)` —
  C# cannot infer `TAggregate` from `Captured<AggregateExecution<TAggregate>>` and leave
  `TMessage` explicit. That is the sample-local helper's problem; #104's `CritterStackFixture`
  should shape it differently (a message-typed capture, say).
- *Tags are strings.* `[Scenario(Tags = ["retry(2)"])]` is the Gherkin vocabulary verbatim, which is
  the point, but there is no IntelliSense for the tag names. A `Retry = 2` property would be nicer
  and was not needed by any port.

## Candidly: are these specs better than the xUnit originals?

The thesis being tested. Three axes.

**Rendering — yes, clearly.** An xUnit test that fails tells you the first assertion that failed,
with a stack trace. The code-first spec renders the *whole* scenario: the events that were
appended as a table, the command as a row, each assertion with expected and actual, and the
duration of the step that waited. Deliberately breaking three assertions in the aggregate port gave
(from `dotnet run -- report --feature Aggregate`):

```
  Events then response FAILED
    ✓ Given a LetterAggregate stream 0c6e30e5 with these events (44ms)
      │ 1 │ LetterStarted │ OK │
    ✓ When  RaiseABC is received for 0c6e30e5 (706ms)
      │ 1 │ 0c6e30e5-8d63-4ba5-bf98-cfcf61af9eba │ OK │
    ✗ Then  these events are appended (2ms)
      │ 1 │ AEvent                          │  OK  │
      │ 2 │ BEvent                          │  OK  │
      │ 3 │ expected 'DEvent', got 'CEvent' │ FAIL │
    ✓ Then  the Response that was sent should carry ACount 1
    ✗ Then  the aggregate's ACount should be 2
        ✗ result: expected '2', got '1'
    ✗ Then  the aggregate's BCount
        ✗ assertion: BCount should be at least 5 but was 1
    ✓ Then  the aggregate's CCount should be 1
  Failed with Rights: 8, Wrongs: 6, Errors: 0
```

Three disagreements, all reported, the passing assertions around them still visible. The xUnit
original would have stopped at the first `ShouldBe`. This is the same report the Gherkin side
gets, and it is what the viewer and the JSON output carry.

**Failure reporting — yes, with one honest caveat.** Gathered assertion failures and typed
failed-vs-error are better than a stack trace. The caveat: the decision to treat any exception in a
`Then` as an assertion failure means a genuinely broken Then (a null reference while *computing* the
value) is reported as `failed` rather than `error`. The message names it, so it is not hidden, but
a supervisor policy keying off failed-vs-error sees it as a disagreement. That is the price of
gathering, and it is the right price for a Then; it is why Given/When keep the critical rule.

**Supervision — yes, for free, and this is the part the originals cannot have.** Because the
specs land on `FeatureDefinition`, they are MTP nodes with stable uids (`Order saga/Starting an
order`), they carry `retry(N)`/`isolated` as traits, they can be run alone by uid, re-run in a fresh
process, or scheduled in isolation by the supervisor — none of which needed a line in the sample.
The one thing to be honest about: nothing here proves the ports *needed* supervision; they are
stable. What is proven is that a spec written this way arrives with the whole resilience layer
attached, where an xUnit class arrives with `IAsyncLifetime`.

**Where the originals are still better:** brevity for a one-assertion test (`should_exist` is one
line of xUnit and three of spec), IDE debugging of a single method (compose-then-execute puts a
lambda between the breakpoint and the step), and the absence of a small learning curve around
"declare, don't await". For a one-off unit-ish test, xUnit wins; for an integration scenario with
more than one step and more than one assertion — the tests that are slow and the tests that flake —
the spec is better on every axis that matters once the test is red.

## Deliberately not built

- A fluent data-setup builder. `Given(text, body).WithRows(records)` covered every port.
- Binding code-first steps to `[Given]/[When]/[Then]` fixture methods by text (the notes.md
  `[FormatAs]`). Hosting a fixture and calling its methods inside step bodies covers sharing; a
  second matching engine would not pay for itself.
- A `Retry`/`Timeout`/`Isolated` property set on `[Scenario]`. Tags are the vocabulary the whole
  resilience layer reads; typed sugar can come when someone wants it.
- Making `BobcatRunner.ScanForFeatures` find specifications. It is an extension
  (`ScanForSpecifications`) to keep the core change additive while other agents are in
  `BobcatRunner`; folding it in is a one-line follow-up once the API settles.
- Wiring `BobcatLoggerProvider` into the hosts so the saga's `ILogger` lines land on the step. The
  provider exists in core but the runner never calls `SetContext`; the sample hosts log at Warning
  for now. Worth doing — notes.md lists log correlation as a goal — but it is a runner change.

## Open questions for when the API settles (#105 stays open)

- Should the generator's throwing-`[Then]` semantics move to match (assertion failure, continue)?
  The twin test would still pass; the question is whether existing Gherkin suites rely on the stop.
- `CritterStackFixture` (#104) should absorb `CritterStackSpecification.cs` from the sample and
  delete it; the shapes to keep are `GivenStream`'s rows, `WhenCommand`'s capture, and
  `ThenNewEvents`' ordered table.
- `ShouldSatisfy` with a projector; a `Retry =` property on `[Scenario]`; `ScanForFeatures`
  finding specifications — each one line, each waiting for a second asker.
