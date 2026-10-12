# Migrating from Gherkin

Bobcat used to have two ways to write a specification: a Gherkin `.feature` file bound to a
`Fixture` class, run on Bobcat's own engine, and the code-first model this documentation now
describes. As of Bobcat 0.50 ([bobcat#471](https://github.com/JasperFx/bobcat/issues/471)) there is
only the second. The `.feature` lane is gone: the Gherkin half of the generator, the engine, `Fixture`,
`BobcatRunner`, `CritterStackFixture`, and the `Bobcat.Mtp`, `Bobcat.Alba` and
`Bobcat.EntityFrameworkCore` packages.

<!-- JEREMY -->
Why: two authoring models meant two execution models, two renderers kept in agreement by tests, two
lifecycles, and every new capability built twice. Nearly everything the Gherkin lane did had a
code-first twin already, and the code-first model is what real suites were adopting.

This page maps each piece of a Gherkin suite to its code-first equivalent, and then lists the places
where the behaviour is different and not merely spelled differently.

## The shape of a suite

| Gherkin | Code-first |
|---|---|
| a `.feature` file plus a `Fixture` class | one test class |
| `Feature: Customer registration`, `[FixtureTitle]` | `[BobcatFeature("Customer registration")]` on the class |
| `Scenario:` | a test method -- `[Fact]`, `[Test]`, `[TestMethod]` |
| `Scenario Outline:` + `Examples:` | a parameterized test -- `[Theory]` with `[InlineData]` rows, or your runner's equivalent |
| `Background:` | a helper method the tests call, or the class constructor |
| `@slice:ConfirmAppointment`, `@domain:`, `@chapter:`, `@pattern:` | `[BobcatSpec(typeof(ConfirmAppointment), Domain = …, Chapter = …, Pattern = …)]`, or `[BobcatSlice]` beside your own test attribute -- see [Binding a specification to a slice](marker-steps.md#bobcatslice) |
| `Triggered by the fraud desk` | `TriggeredBy = "the fraud desk"` on `[BobcatSpec]` or `[BobcatSlice]` |
| a scenario with no steps (pending) | `[BobcatSpec(typeof(X), Pending = true)]` |
| type captures (`{aggregate}`, `{command}`, `{event}`, …) stamping the Event Model | the vocabulary's `[EventModelRole]` attributes, read off the methods a test calls -- see [Binding, not roles](marker-steps.md#event-model-roles) |
| `[Given("…")]` / `[When]` / `[Then]` / `[Check]` on a `Fixture` method | the same attributes on a step helper, on any class -- `internal` or `public`, because the call is intercepted |
| `[IncludeGrammars(typeof(…))]` modules | ordinary C#: a base spec class, or a helper object the test holds |
| `Context!` / `IStepContext` | the host's services, from your fixture or `Services` on a spec |

## The project

| Gherkin | Code-first |
|---|---|
| `Bobcat.Mtp`, and the generated entry point | your test framework's own (`xunit.v3`, TUnit, NUnit, MSTest) plus its Bobcat adapter: `Bobcat.Xunit`, `Bobcat.TUnit`, `Bobcat.NUnit` or `Bobcat.MSTest` |
| `<AdditionalFiles Include="Features/**/*.feature" />` | nothing -- delete it |
| `[BobcatConfiguration] Configure(BobcatRunner runner)` | your test framework's fixtures: an xUnit assembly, collection or class fixture, `IAsyncLifetime`, TUnit's hooks |
| `runner.Resources.Add(…)`, `AlbaResource<Program>`, `ITestResource` | a fixture that boots the host -- `AlbaHost.For<Program>()` directly, for an ASP.NET Core application |
| a resource's reset hook, `ResetBetweenScenarios` | mint new ids per test; where that isn't enough, `ResetAsync()` on the spec or `host.ResetEventStoresAsync()` from the fixture |
| `runner.Preflight` (in-process) | `supervisor.Preflight` under the [supervisor](parallel-ready-suites.md); not in-process (see below) |
| `runner.RetryBudget` | `supervisor.RetryBudget`, and the `Retry` / `Isolated` / `RecycleOnRetry` traits for `@retry` / `@isolated` / `@recycle` |
| `Bobcat.Alba`'s `Context.PostJsonAsync(…)` | `WolverineHttpSpec.WhenPosted(…)`, or your own act over Alba -- see [Your own acts](integrations/wolverine.md#your-own-acts) |
| `Bobcat.EntityFrameworkCore`, `[EfCoreEntities]` recipes | dropped, no replacement |

## The command line

| Gherkin | Code-first |
|---|---|
| `./MySpecs run`, `dotnet test` | `dotnet test`, or the test executable |
| `run --feature`, `--tag`; `--filter-feature`, `--filter-tag`, `--filter-uid` | your framework's filters -- `--filter-class`, `--filter-method`, `--filter-trait` on xUnit v3 |
| `preview` | `BOBCAT_SPEC_PREVIEW=1 ./MySpecs --list-tests` |
| the rendered console report | printed at process exit in a terminal; `BOBCAT_SPEC_CONSOLE=1` forces it |
| `run --json` | `ProjectedSpecConsole.Captured` and `JsonRenderer.RenderScenario`, for a consumer that wants the JSON |
| `interactive` | your IDE's test explorer, or a [resident runner](resident-runner.md) in `warm` mode |
| `./MySpecs --resident`, `BOBCAT_RESIDENT=1` | `bobcat resident ./MySpecs` -- see [The Resident Runner](resident-runner.md) |

## The Critter Stack grammar, step by step

`CritterStackFixture` (and Wolverine's `WolverineCritterStackFixture`) become `CritterStackSpec` (and
`WolverineSpec`). Every shipped step has a method. A step's trailing table of fields becomes a
partial object -- `Specify<T>().With(x => x.Prop, value)`, or `Specify<T>(table)` to keep the table
-- or a whole object, compared structurally.

| Gherkin step | Code-first |
|---|---|
| `Given no events for {aggregate} {string}` | `await GivenNoEventsFor<TAggregate>(id)` |
| `Given events for {aggregate}` + an `Event` table | `await GivenEvents<TAggregate>(id, event1, event2, …)` |
| `Given {event} occurred` (+ fields) | `await GivenEvents<TAggregate>(id, new SomethingHappened(…))`, or `Specify<SomethingHappened>().With(…)` |
| `When {command} is received` (+ fields) | `await WhenReceived(command)` on `WolverineSpec`; `await WhenActing(description, act)` with no bus |
| `Then {event} is emitted` (+ fields) | `ThenEmitted<T>()`, or `ThenEmitted(Specify<T>().With(…))` |
| `Then exactly these events are emitted` | `ThenEvents(…)` |
| `Then these events are emitted in any order` | `ThenEventsInAnyOrder(…)` |
| `Then {event} is not emitted` (+ fields) | `ThenNotEmitted<T>()`, or `ThenNotEmitted(Specify<T>().With(…))` |
| `Then no events are emitted` | `ThenNoEvents()` |
| `Then validation fails with {string}` | `ThenValidationFails(reason)` |
| `Then the command is refused` | `ThenCommandRefused()` |
| `Then a {aggregate} stream is started with id {string}` | `await ThenStreamIsStarted<TAggregate>(id)` |
| `Then the {readmodel} read model contains` | `await ThenSingleReadModel<T>(Specify<T>(table))` |
| `Then the {readmodel} read model with id {string} contains` | `await ThenReadModel<T>(id, Specify<T>(table))` |
| `Then {message} is sent` | `ThenMessageSent<T>()` on `WolverineSpec`, or `ThenMessagesSent<T>()` |
| `Given documents of type {document}` | `await GivenReadModel<T>(document)` per document |
| `Then the {document} with id {string} has` | `await ThenDocument<T>(id, Specify<T>(table))` |
| `Then no {document} exists with id {string}` | `await ThenNoReadModel<T>(id)` |
| `Given the date is {string}` | `GivenTheDateIs(new DateOnly(…))` |
| `Given the current time is {string}` | `GivenTheTimeIs(DateTimeOffset)` |
| `When the clock advances by {string}` | `WhenTheClockAdvancesBy(TimeSpan)` |
| `When {string} passes` | `WhenTimePasses(TimeSpan)` |
| `When the clock advances to {string}` | `WhenTheClockAdvancesTo(DateTimeOffset)` |
| `Given the arrangement {string}` (named arrangements) | a helper method on your base spec class |

The code-level members of `CritterStackFixture` carry over nearly unchanged:

| `CritterStackFixture` | `CritterStackSpec` |
|---|---|
| `GivenEvents<T>(id, events)` | the same |
| `GivenNoEvents<T>(id)` | `GivenNoEventsFor<T>(id)` |
| `WhenCommand<T>(command)` | `WhenReceived(command)` on `WolverineSpec`, `WhenActing(…)` with no bus |
| `ThenEvents`, `ThenNoEvents`, `ThenValidationFails`, `ThenCommandRefused`, `ThenMessagesSent<T>(count)` | the same |
| `ThenDocument<T>(Action<T> assert)` | `ThenDocument<T>(id, Specify<T>(x => x.Prop.ShouldBe(…)))`, which hands the document back for anything else |
| `TheStreamVersion`, `ThenEventsOn<T>(id, afterVersion, …)`, `ThenNoEventsOn<T>(id, afterVersion)`, `WhenActingOn<T>` | the same signatures |
| `protected virtual ProjectionTimeout` | a settable `ProjectionTimeout` property |
| `WhenTracked(() => …)` (WolverineFx.Bobcat) | `WhenTracked(description, () => …)` |

The full vocabulary, including what the Gherkin grammar never had -- aggregates by `FetchLatest`,
stream-version steps, Dynamic Consistency Boundaries, the boundary steps, and
`ThenEventuallyCaused` -- is on [Bobcat with Wolverine](integrations/wolverine.md).

## Fixture features

| Gherkin | Code-first |
|---|---|
| `[Table]` on a step (one call per row) | a step taking a `StepTable`, handing it to `TableRunner.Run(this, nameof(rowMethod), table)` |
| `[DecisionTable]` + `[Expected("sum")]` | `TableRunner.Run` with a row method that returns the value; or `SpecAssert.Check(column, actual, expected, rowIndex: i)` per row |
| `[DecisionTable]` with `out` parameters | `SpecAssert.Check` per output column, row by row ([#483](https://github.com/JasperFx/bobcat/issues/483)) |
| `[SetVerification(KeyColumns = …, Ordered = …, Column = …)]` | `SetVerificationComparer.Verify(actual, expected, keyColumns: …, ordered: …, column: …)` |
| `Fixture.BuildRows<T>`, `RunTable`, `VerifySet`, `VerifyObject` | `TableRunner.BuildRows<T>`, `TableRunner.Run`, `SetVerificationComparer.Verify`, `PropertyCells.Verify` |
| `[TableGrammar]` with `Before` / `Row` / `After` | a step whose body clears before `TableRunner.Run` and saves after it -- see [A table the step runs itself](tutorials/data-intensive-specifications.md#a-table-the-step-runs-itself) |
| `[ScopePerRow]` | dropped. Open a scope inside the row method if a row needs one |
| `[Header]` | unchanged, on a row method's parameter or a record's property |
| `[WaitFor(timeout)]` | `await SpecAssert.EventuallyAsync(…)` inside the helper |
| `[Approx(0.001)]` | `SpecAssert.Check(name, actual, expected, tolerance: 0.001)` |
| `[Comparison(typeof(MyChecker))]` | `SpecAssert.Check(…, options: new CheckOptions { ComparisonType = typeof(MyChecker) })` |
| `[BeforeEach]`, `[AfterEach]`, `[BeforeAll]`, `[AfterAll]` | your test framework's setup and teardown |
| `IStepContext.AttachDiagnostic`, step logs | no equivalent yet -- a recorded step carries neither |

## What behaves differently

These aren't renames. Each one can change a verdict, so read them before you trust a converted suite.

- **Refusal matching is case-sensitive, and searches inner exceptions.** `ThenValidationFails` and
  `ThenRefusedWith` match a substring with `StringComparison.Ordinal`, where `validation fails with`
  ignored case. They search the exception the act raised *and every inner exception* (each of an
  `AggregateException`'s too), so a refusal wrapped by the act's plumbing is found. A spec that passed
  on a differently cased reason now fails.
- **A sent message matches its subtypes.** `ThenMessageSent<T>()` passes for any message that *is* a
  `T`; `{message} is sent` required the exact type. A spec that relied on a subtype *not* counting
  needs `ThenNoMessageSent<TSubtype>()` or `ThenMessageSent(expected)`.
- **Document and read model reads wait for the projections.** `ThenDocument`, `ThenReadModel` and the
  rest wait for the async daemons to catch up before they load, where `the {document} with id … has`
  read immediately. A spec that asserted a document was *not yet* updated reads a caught-up one now.
- **The projection wait is 20 seconds**, `ProjectionTimeout`, on every scenario --
  `WolverineScenario` used to wait a fixed 5. Lower it per spec if a slow failure costs you.
- **Polling is explicit.** `[WaitFor]` and `[Approx]` do nothing on a helper a test calls, because no
  generated code wraps the call. Use `SpecAssert.EventuallyAsync` and `tolerance:`; **BOBCAT035** flags
  any attribute left behind.
- **Tables are run by `TableRunner`**, not bound at compile time. A cell that won't convert fails its
  row at run time (`BadCellException`) instead of failing the build, and a column a row method doesn't
  name is not a build error. BOBCAT032 still checks a constant table passed to a property check.
- **An extra row in a decision table or a set fails the step, and a throwing row is `error`.** The
  step's verdict now tells a wrong from an exception; the Gherkin lane marked both `failed`.
- **Table recipes and `[ScopePerRow]` are gone**, with `Bobcat.EntityFrameworkCore`.
- **No lifecycle timing points.** The timing summary used to stamp `ResetAll`, `BeforeEach` and the
  rest; your test framework owns the lifecycle now, so the report says "lifecycle not measured". Each
  scenario reports its wall clock beside what its steps cost -- see
  [Where the time went](marker-steps.md#timing).
- **No in-process preflight.** A broken environment used to abort a `dotnet test` run in seconds with
  every unrun scenario named. Under the supervisor, `supervisor.Preflight` still does that before any
  worker launches; a plain `dotnet test` run doesn't have it.
- **A TUnit suite can't be run by identity** ([#470](https://github.com/JasperFx/bobcat/issues/470)).
  It records, renders and publishes, but the resident runner and a console's run button can't drive
  it. xUnit v3 can.
- **Scenarios run in your framework's order, and in parallel if it says so.** The store-only act
  (`WhenActing`) reads back everything appended while it ran, so a suite on it runs its tests one at a
  time against a shared store, or gives each test its own.

## Diagnostics that went with the lane

BOBCAT001 through BOBCAT022 were the Gherkin generator's -- an unbound step, a feature with no fixture,
an ambiguous match, an unknown arrangement, and the rest -- and they went with it. The diagnostics
that remain are listed in [Writing Specifications](marker-steps.md#diagnostics).
