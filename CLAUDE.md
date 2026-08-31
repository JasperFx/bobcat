# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is Bobcat?

Bobcat is a spec-driven integration testing framework for .NET, successor to Storyteller. `.feature` files are compiled to direct fixture method calls via a Roslyn source generator — no runtime reflection, compile-time step matching with compile errors for unmatched steps.

## Build & Test Commands

```bash
# Build everything
dotnet build

# Run unit tests
dotnet test

# Start the local Postgres the Marten integration tests use (published on 5445 so it
# never collides with a Postgres you already run). Without it those tests SKIP locally
# — on CI they never skip, so a missing database fails the build.
docker compose up -d

# Run the spec runner demo (uses source-generated code from .feature files)
dotnet run --project src/ConsolePreview/ -- run

# List discovered features/scenarios
dotnet run --project src/ConsolePreview/ -- list

# Filter by feature name
dotnet run --project src/ConsolePreview/ -- run --feature "Calculator"

# Filter by tag
dotnet run --project src/ConsolePreview/ -- run --tag regression

# Run a specific test class. Tests run on Microsoft.Testing.Platform, not VSTest, so the
# old `--filter "FullyQualifiedName~X"` is gone. Arguments after `--` go to the test host.
dotnet test src/Bobcat.Tests/ -- --filter-class "*PipelineTests"
dotnet test src/Bobcat.Tests/ -- --filter-method "*passes_on_retry*"

# A test project is a self-executing MTP application, so it can also be run directly:
./src/Bobcat.Tests/bin/Debug/net10.0/Bobcat.Tests --list-tests

# Inspect generated source (look in obj/Debug/net10.0/generated/)
```

All projects target .NET 10.0 except Bobcat.Generators (netstandard2.0). Tests use **xUnit v3 +
Shouldly + NSubstitute**, running on **Microsoft.Testing.Platform** rather than VSTest.

Every `*.Tests` project is therefore a self-executing MTP test host — `OutputType=Exe`,
`UseMicrosoftTestingPlatformRunner`, `TestingPlatformDotnetTestSupport` are set once in
`src/Directory.Build.props` for any project whose name ends in `.Tests`, so a new test project
cannot silently fall back to VSTest. There is no `Microsoft.NET.Test.Sdk`, no
`xunit.runner.visualstudio`, and no `coverlet.collector` (a VSTest data collector) — MTP supplies
its own equivalents.

**Version pin that matters:** `Microsoft.Testing.Platform` is held at **1.9.1** because that is
what `xunit.v3` 3.2.2 builds against (its package is literally `xunit.v3.core.mtp-v1`). Moving it
to 2.x loads a platform assembly whose types have moved and xUnit's auto-registered MSBuild
extension dies with a `TypeLoadException` on `IDataConsumer`.

**Database-backed tests:** `Bobcat.Marten.Tests` exercises the `[MartenEntities]` recipe against a
real Postgres via `[PostgresFact]`. Connection string comes from `BOBCAT_POSTGRES`, defaulting to
the `docker-compose.yml` instance on **5445**. The skip is deliberately disabled when `CI=true`,
so CI can never report a silent pass for a missing database.

**Generator note:** every type name emitted *as a type* (casts, generic args, local declarations,
`default(...)`, `typeof(...)`, `new`) must use `ParameterInfo.QualifiedType` /
`QualifiedReturnType` / the `global::`-qualified class FQNs. Generated code lives in the fixture's
own namespace, where an unqualified name binds to the wrong type — e.g. `Marten.IDocumentSession`
resolving to `Bobcat.Marten.IDocumentSession` inside `Bobcat.Marten.Tests`. The plain `Type`
string stays short ("int", "string") because the Gherkin literal conversion switches on it.

## Naming conventions

- **Pascal casing for all public or internal members** — `RunAll()`, `RetryBudget`, `Uid`.
- **camel casing for all private or protected members** — `runPreflight()`, `_sharedWorker`,
  `builtInTypes`.

Accessibility is therefore visible at every call site: an uppercase call is reaching outward, a
lowercase one is staying inside the type. That is the point of the rule — you can see the seam
without chasing the declaration.

Two clarifications the rule does not state, both settled by what the codebase already did:

- **Private instance fields keep the `_` prefix** (`_features`, `_lanes`, `_gate`).
  Underscore-camel *is* the camel form here; it matches the wider JasperFx codebase, where the
  split runs 262 to 3. Statics and consts that never carried one do not gain one — `builtInTypes`,
  `standardErrorLinesKept`.
- **Types stay Pascal regardless of accessibility**, including private nested ones (`StubResource`,
  `FakeWorker`). "Member" means fields, properties, methods, events and consts — not types.
  Lower-casing a type name reads as a variable everywhere it is used.

Not renamed, deliberately: `override` members (the base declares the name), entry points (`Main`),
and anything a source generator emits. The only generated private members in the build today come
from .NET's `[GeneratedRegex]` (see `Fixture.cs`) — Bobcat's own generator emits none, so there is
nothing to fix in its templates.

The codebase was converted in one pass using a Roslyn `Renamer`, not by hand or by regex — a
textual rename of a name like `Record` would have hit unrelated identifiers. The same tool is the
right way to sweep up after a long-lived branch merges.

One gotcha when doing that: a member whose camel form is a C# keyword (`Class` → `class`) is
escaped to `@class` rather than rejected, so it compiles and the sweep looks clean. Grep the diff
for `@` identifiers afterwards and give those a real name instead — `testsInClass`, not `@class`.

## Architecture

### Source Generator Pipeline (primary authoring flow)

`.feature` file → **Bobcat.Generators** (compile time) → generated C# with direct method calls → **Executor** (runtime) → **SpecRender** → console/HTML output

1. **Feature files** are `<AdditionalFiles>` in the consuming project's `.csproj`
2. **Source generator** (`BobcatGenerator`) reads features + fixture Roslyn symbols in same pass
3. **Cucumber Expression parser** matches step text to `[Given]`/`[When]`/`[Then]` methods at compile time
4. **Generated code** creates `FeatureDefinition` with `DelegateExecutionStep` lambdas (no reflection)
5. **BobcatRunner** discovers generated features, manages resources, executes via `Executor`, renders results

### Fixture → Feature Mapping
One fixture per feature. Matched by `[FixtureTitle("...")]` attribute or naming convention (`OrderAggregateFixture` → "Order Aggregate").

### Step discovery walks base classes — base class *or* `[IncludeGrammars]`, both ship (issue #104)
The generator discovers `[Given]/[When]/[Then]/[Check]` methods declared on a fixture **and on its
base classes**, stopping at `Bobcat.Fixture`/`object` (`BobcatGenerator.collectStepsAndHooks`). This
works across an assembly boundary — a base fixture in a referenced package is read from metadata —
so a shipped grammar base class binds from the NuGet reference alone, no source needed by the
*generator* (the editor is another matter; see "ship as source" below).

- **Most-derived wins** on duplicate step text (keyword + expression): a derived `[Then("the label
  is {string}")]` hides the base one, and the hidden base step is reported as **BOBCAT015** (info,
  not an error). Method overrides/`new`-hiding are de-duplicated by signature so an `override` is not
  double-counted.
- **Lifecycle hooks nest**: discovered `BeforeEach`/`BeforeAll` run base-first, `AfterEach`/`AfterAll`
  derived-first — constructor/disposer order.
- **Two composition mechanisms, and which is canonical:** a **base class** (`class WithdrawFunds :
  CritterStackFixture`) is canonical for "this fixture *is* a … fixture" — the whole vocabulary with
  no attribute. `[IncludeGrammars(typeof(Module))]` is for **mix-ins** — a shared module dropped into
  a fixture that already has another base, or several grammars combined. Modules inherit their own
  base's steps too, so an empty `sealed class XGrammars : XFixture` exposes a base fixture as a module.
- **Type-name captures** — `{type}`, and the Event Modeling aliases `{aggregate}`/`{command}`/
  `{event}`/`{readmodel}`/`{message}` — capture a type *name* in the step text and bind to a
  `System.Type` parameter as `typeof(global::…)`. `TypeNameResolver` resolves the name against the
  consuming compilation and its non-framework references: a dotted name matches a full name; a simple
  name must match exactly one type by simple name. Unresolved is **BOBCAT011**, ambiguous is
  **BOBCAT012** (qualify it in the step text). Ambiguous *step* matches are **BOBCAT013**; a
  `[SetVerification]` step with no table is **BOBCAT014**.
- **`Bobcat.StepTable`** — declare a `StepTable` (nullable when optional) parameter to receive the
  step's whole trailing data table as one argument (headers + rows as written), instead of the
  per-row `[Table]` calls. Never binds a column, never resolved from DI. Built for grammars whose row
  shape is not fixed at compile time (event records of several types, a command bound by column name).

### Event Modeling slice tags and descriptors (`SliceTags`, issues #104/#106)

A feature declares the non-derivable bits of an Event Modeling slice: `@slice:<name>` and
`@domain:<name>` tags, and a `Triggered by …` description line. The parser carries feature tags
(inherited by every scenario, standard Gherkin) and the description onto
`FeatureDefinition.Tags`/`Description`, surfaced as `Slice`/`Domain`/`TriggeredBy`.
`ResilienceTags` projects any `key:value` tag onto a `key = value` trait, so `@slice:` reaches a
supervisor/viewer with no Bobcat reference.

**The generator turns these into a JasperFx `EventModelSliceDescriptor` (issue #106)** —
Bobcat is the first real implementation of `IEventModelDefinitionSource` anywhere. One
`BobcatEventModelSource.g.cs` per assembly, registered with `services.AddBobcatEventModel()`.

- **`[assembly: EventModelName("…")]` names the model; the assembly name is only the fallback
  (issue #172).** Upstream, `EventModelDiscovery.Assemble` folds descriptors together **by model
  name**, and the Wolverine-derived model is named for the *service* (`opts.ServiceName`) — so a
  spec assembly called `BankAccountES.Tests` can never merge with the chains it describes unless
  it declares the service's name. The attribute renames only the descriptor; the source's
  `Subject` URI keeps the assembly name, because two spec assemblies may legitimately feed one
  model. `samples/BankAccountES` is the worked example and its `EventModel.feature` pins the
  merge end to end.
- **The generated source is `internal`, and that has a consequence:** the app host cannot see the
  spec assembly's slices (the reference points the other way), so composing all three design-time
  sources — chains + overlay + specs — currently only works in the spec-runner's process, where
  `samples/BankAccountES/Tests/EventModelFixture.cs` does it. The host's own
  `event-model --url` export (and therefore `bobcat watch-event-model`) pushes a document
  *without* the spec-declared slices or their `Specifications` bindings. Closing that gap is
  follow-on work under issue #172.
- **An overlay's human trigger label on an HTTP slice works again as of WolverineFx 6.31.0**
  (wolverine#4181/#4182, both fixed by wolverine#4185). Before it, the HTTP-derived source
  claimed `TriggerLabel` with the verb+route, so the overlay's label lost the merge and minted a
  noise `SourceDisagreement` hotspot per labelled slice — and a query endpoint returning
  `IReadOnlyList<T>` reported the raw generic CLR string as its read model. `samples/BankAccountES`
  encoded that workaround (no `TriggeredBy` on HTTP slices) with `EventModel.feature` asserting
  the *broken* behaviour so the fix would trip a spec rather than go unnoticed. It did: both
  tripwires are flipped, the six overlay labels are restored, and the sample is pinned to 6.31.0.
  The scenarios that now assert the fixed behaviour are the regression guard — don't delete them
  as redundant.

- **A slice is a scenario-level grouping, not a feature-level one.** A feature is a document; a
  slice is a vertical behaviour, and several specs usually describe the same one —
  `Wallet.feature` tags `@slice:CreditWallet` three times. Because `SimpleGherkinParser` already
  merges feature tags into every scenario's tag list, reading the tag off the *scenario* handles
  both placements with one rule, and a slice may legitimately span several feature files. Slices
  accumulate across the whole compilation, so two features feeding one slice yield one descriptor.
- **Only roles are stamped, never a graph.** `EventModelSliceDescriptor.Elements`/`.Edges` are
  *computed upstream* from the typed roles on every read. Building the element graph in the
  generator would be a second opinion about the same slice, which is what "computed on read"
  exists to prevent.
- **The command is the act — the last `{command}` on a `When`, not the first one named.** Specs
  routinely arrange by issuing earlier commands (`When OpenWallet is received` before the
  `When CreditWallet is received` the scenario is about), so first-wins mislabelled the slice.
  Arrange commands are not lost: they stay in the specification's `ResolvedTypes`, which is where
  "this spec touched that type" belongs and what #107's run evidence joins on.
- **Gated on the reference, by type probe.** Nothing is emitted unless the consuming compilation
  has `JasperFx.Events.EventModeling.EventModelSliceDescriptor` — most Bobcat suites do no event
  sourcing. A type probe rather than an assembly-name check because JasperFx.Events **2.53.0
  shipped an early, incompatible sketch** of that namespace; "the assembly is referenced" and
  "the shape emitted against exists" are different questions. Requires **2.54.0+**.
- **Spec identity is `{Feature}/{Scenario}`** — the same string `SpecNodeMapping.Uid` produces and
  `scenario_finished` carries, so design-time and run evidence join with no mapping table.
- **A scenario with no steps is a pending-specification hotspot** (jasperfx#689). A scenario whose
  steps do not *match* is not this case — that is already a compile error and the whole feature is
  skipped first.
- **`Pattern` is derived only where Gherkin can tell**: a slice receiving a command is `Command`,
  one that only asserts a read model is `View`. `Automation` and `Translation` need a trigger
  Gherkin does not express, so they stay null — a wrong pattern miscolours the canvas, a null one
  does not.
- **`ParameterCapture.ParameterName` exists for this.** The six type-name words all share one
  `CSharpType` (`System.Type`), so without the word a resolved capture says "this is a type" but
  not *which role* — and a `{command}` and an `{event}` land in different slots and different
  lanes. Every step binding stayed correct while that distinction was being discarded.
- **`GeneratorSliceTags` duplicates `Bobcat.Runtime.SliceTags`** because the netstandard2.0
  generator references nothing. `SliceTagParsingAgreementTests` pins them together (the source is
  `<Compile Link>`-ed into `Bobcat.Tests` rather than referencing the analyzer assembly). Without
  it the runtime and the descriptor could report different slice names and nothing would say so —
  same guard as `ResourceParsingAgreementTests`.
- **Code-first specs contribute slices too (issue #170).** `CodeFirstSpecs.Extract` reads
  `[Scenario]` methods on non-abstract `Specification` subclasses (base classes included; a
  partial class speaks once) and `EventModelEmitter.Collect` folds them into the *same* slice
  dictionary the features feed — so one slice fed by a `.feature` and a C# spec is one
  descriptor. Roslyn-side rather than a runtime-contributed source, deliberately: compile-time
  extraction keeps identity stamping identical for both authoring styles, which is what lets
  run evidence join the same way. Slice/domain come from `[Scenario(Tags = ["slice:X",
  "domain:Y"])]` (same vocabulary, no `@`); identity is `{derived feature title}/{derived
  scenario title}` via `CodeFirstNaming` — the generator's verbatim copy of
  `SpecificationFeature.DeriveTitle`/`DeriveScenarioTitle`, pinned by
  `CodeFirstNamingAgreementTests` (linked source, same pattern as `GeneratorSliceTags`). Roles
  come from the typed-step convention in the method body, lambdas included
  (`Host<TFixture>()`-borrowed steps): `GivenEvents<T>`/`GivenNoEvents<T>` → aggregate,
  `WhenCommand<T>` → aggregate + the argument's static type as command (last one is the act,
  same last-When rule), `ThenEvents(...)` → argument types as events, `ThenDocument<T>` →
  readmodel, `ThenMessagesSent<T>` → message — matched by name but gated on the target being
  declared on a `Bobcat.Fixture` subclass, so an unrelated `WhenCommand` never stamps a phantom
  role. Arrange-event arguments to `GivenEvents` are deliberately not stamped (the Gherkin path
  resolves only the aggregate there — same spec, same shape). An empty `[Scenario]` method is
  the pending-specification hotspot; an untagged scenario with no roles contributes nothing;
  there is no trigger label (code-first has no `Triggered by` line). An `object`-typed or
  unresolvable command argument degrades to no role, never a guess.

### Step Attributes (`src/Bobcat/Attributes.cs`)
`[Given("...")]`, `[When("...")]`, `[Then("...")]`, `[Check("...")]` using Cucumber Expression syntax (`{int}`, `{string}`, `{word}`, raw regex). `[Table]` for table data steps. `[SetVerification(KeyColumns = "...")]` for set comparison.

### Table Grammar (`[TableGrammar]`)
`[TableGrammar("step text")]` on a **class** gives one grammar a Before-once / per-row /
After-once envelope inside a single scenario — batched setup and decision tables. Surface syntax
is a normal Gherkin step plus a trailing `|...|` table; no new keywords. Internals are discovered
by convention (`Before` / `Row` / `After`, `Async` suffix recognized; `[Before]`/`[Row]`/`[After]`
override). Table grammars are discovered across the compilation and matched by step text alone —
keyword-agnostic, so a shared grammar drops into any feature.

- Fresh instance per execution, so `Before`'s session and `After`'s save share fields.
- Columns bind to `Row` parameters by header name; a type no cell can produce is injected from
  the scenario scope. `[ScopePerRow]` on the class gives each row its own child scope.
- **Decision-table disambiguation:** `Row` returns a value + exactly one unbound column → that
  column is the *expected* output, compared via `CellCheck`. `[Expected("col")]` on `Row` names it.
- **Failure tiers:** `Before` throws → critical (rows skipped, `After` still runs); per-row
  comparison failures gather and render the full table; `SpecCatastrophicException` stops the
  suite. `After` always runs in a `finally`.

### Persistence Recipes
A recipe attribute on a `[TableGrammar]` class auto-supplies the envelope plus a per-row
persistence sink, so a data-setup table needs almost no code:

```csharp
[TableGrammar("the following customers exist")]
[MartenEntities<Customer>]          // or [EfCoreEntities<Customer>(ContextType = typeof(ShopContext))]
public class CustomerEntities { }   // no Row body — columns bind to Customer's constructor
```

- **Seam:** `IGrammarBehavior` (Open / Row / Close) + `GrammarBehaviorAttribute` in core. The
  netstandard2.0 generator must never reference Marten or EF; it only recognizes "this attribute
  derives from `GrammarBehaviorAttribute`" and emits a generic envelope call. `GrammarBehaviors.Resolve`
  is the one runtime-resolved piece — the accepted, bounded softening of "no reflection".
- **The behavior** lives in the extension package and resolves its session/context from
  `IHostResource.CurrentServices`, so the recipe's session and a hand-injected
  `[FromScopedService] IDocumentSession` are the **same instance** — that is what batches the save.
- **Entity construction** is compile-time: columns bind to the entity's constructor parameters
  first (records-friendly), then to settable properties, by header name. A hand-written `Row`
  returning the entity is the override for custom construction. With a recipe applied, `Row`'s
  return means "a product to persist", never an expected value.

### Lifecycle (convention-discovered)
Declare hooks on the fixture by name — the generator emits the calls with parameters resolved
by type, so there is no reflection:
- **Per scenario:** `BeforeEach` / `AfterEach` (+`Async`). Runs *inside* the scenario's DI scope,
  so it injects the same scoped services the steps see.
- **Per feature:** `static BeforeAll` / `AfterAll` (+`Async`). Runs before any scenario scope
  exists — may inject `IStepContext`, test resources, and `[FromRootService]` values, but a
  scoped ask is a compile error (BOBCAT004).
- **Per run:** `IGlobalAction` registered explicitly with `suite.AddGlobalAction(...)`. `SetUp`
  runs after `StartAll` and before the first feature; `TearDown` after the last feature and
  before `DisposeAsync`, in reverse order. Resource-shaped work belongs in an `ITestResource`.

`[BeforeEach]`/`[AfterEach]`/`[BeforeAll]`/`[AfterAll]` override the naming convention. There is
no discovered "system" class, and no `virtual Fixture.SetUp()/TearDown()`.

**Important:** `[Check]` (not `[Fact]`) for boolean assertions — avoids xUnit collision.

### Engine (`src/Bobcat/Engine/`)
- **`Executor`** — Sequential step execution with timeout, cancellation, `IContinuationRule[]`, `IExecutionObserver`
- **`IStepContext`** — Narrow interface for fixture code: `GetService<T>()`, `GetResource<T>()`, `Log()`, `AttachDiagnostic()`
- **`DelegateExecutionStep`** — `IExecutionStep` backed by lambda (target for generated code)
- **`StepKind`** / **`FailureLevel`** — drives automatic failure classification
- Auto-marks success when steps complete without error
- **In-flight progress (issue #99):** `IExecutionObserver.ScenarioStarted(feature, scenario,
  totalSteps)` is a default member the runner calls (plan is built first, so the count is a
  fact) that forwards to the two-argument form; `StepUpdate.Row`/`TotalRows` (via
  `StepUpdate.ForRow`) is what the generated `[TableGrammar]` envelope reports before each row,
  message-less so the console stays quiet. `MonitorPublishingObserver` maps these onto
  `step_progress` (coalesced to one per 100 ms per step, first and last row always) plus
  `TotalSteps`/`StepNumber`/`ScenarioElapsedMs` on the existing events. Details in
  `docs/monitor-design.md`, Bobcat-side seams item 5.
- **Run evidence (issue #107):** `IStepContext.RecordTouchedType(Type)` (default no-op)
  accumulates onto `ExecutionResults.TouchedTypes` — first-touch order, deduplicated — and
  travels on `scenario_finished` as `TouchedTypes` (JasperFx `TypeDescriptor`'s three fields,
  mirrored into both contract copies, `FullName` joining a design-time
  `SpecificationDescriptor.ResolvedTypes` on the #106 descriptor) plus the `At` finish stamp.
  Observed, never asserted: `Bobcat.CritterStack`'s typed steps record the aggregate arranged,
  the command dispatched, the events the stream gained, the messages the tracked session sent,
  the read model loaded — never what a `Then` merely names. Nothing recorded is null, not an
  empty list. Exposed per scenario by `GET /api/runs/{id}`; CTRF/JUnit untouched. Details in
  `docs/monitor-design.md`, Bobcat-side seams item 6.
- **Step timeline (issue #141):** one wall clock per attempt, owned by the runner and zeroed at
  the `ScenarioStarted` announcement — which now fires *before* `ResetAll` (the plan is built
  first, so the step count was already a fact). The `Executor` stamps step offsets from that
  shared clock (`Executor` ctor's optional `scenarioClock`; a bare executor keeps its own,
  zeroed at `Execute`), `IExecutionObserver.StepStarted` gained a four-argument overload
  carrying the offset, and `MonitorPublishingObserver`'s second scenario stopwatch is gone —
  the wire's `ScenarioElapsedMs` is now literally `StepResult.Start`/`End`, so wire and report
  agree by construction (null, not zero, from a three-argument caller with no clock). Lifecycle
  work that is not a step is captured as named `ExecutionResults.Timeline` points (`ResetAll`,
  `BeginScenarioAll`, `BeforeEach`, `AfterEach`, `EndScenarioAll`) on the same clock;
  `ExecutionResults.WallClockMs` is the bracket's true duration, which `SpecRender.DurationMs`
  now reports (falling back to `max(step.End)` — honestly under-reporting — only for results
  with no bracket). `JsonStepOutput.StartedAtMs` + the scenario's `lifecycle` block are the
  persisted artifact #142's analysis reads. Deliberately not captured: per-row table-grammar
  stop points (too fine), and anything for foreign MTP workers — steps are a Bobcat concept,
  and a consumer must degrade to "not measured", never zero-fill.
- **Timeline analysis (issue #142, items 1–3 only):** `SuiteTiming` (`Runtime/SuiteTiming.cs`)
  is the in-process sibling of the supervisor's `RunTiming` — pure computation over
  `SuiteResults`, rendered as `CommandLineRenderer.RenderTimingSummary` (a compact console
  block; a 100ms display floor on gaps, count of the rest noted) and the uncapped `timing`
  block in `JsonRenderer.RenderSuite`. Report, don't act — same guardrail as `RunTiming` and
  #44's hints. Three facts, no heuristics: **gap ranking** (time no step or lifecycle point
  owns, a subtraction attributed by its neighbours), **per-step aggregation** grouped by
  *normalized* step text (`NormalizeStepText` folds quoted strings/bare numbers to
  placeholders, because results carry rendered text, not the Cucumber expression — word-embedded
  digits and dotted versions survive) plus lifecycle points by name (the ResetAll line is the
  headline in a database-backed suite), and **scenarios that assert nothing** (no Then-kind
  step, no comparison cell; `Counts.Rights` is deliberately not the signal — the executor
  auto-marks every completed step `success`; a zero-step scenario is #106's pending hotspot,
  not this list; and the check is honestly blind to foreign MTP workers — #56's own
  `try_it_out` example arrives as a bare duration and cannot be caught here). Figures describe
  the final attempt (retry cost is `RunTiming`'s). "Measured" means the results carry a wall
  clock *or* timeline points — a sub-millisecond scenario legitimately reads 0ms and is not
  "unmeasured". Item 5 (cross-run trends) now ships on the committed ledger
  (`TestLedger.Trends()`, `docs/ledger-design.md`); item 4 (sleep-shaped durations) stays
  unbuilt — its false-positive question is open, and `[SlowByDesign]` is a JasperFx.Testing
  decision.

### Runtime (`src/Bobcat/Runtime/`)
- **`BobcatRunner`** — CLI entry point. Discovers features, manages suite lifecycle, renders results.
- **`FeatureDefinition`** / **`ScenarioDefinition`** — compiled feature structure from generator
- **`TestSuite`** — Named resource registry (start/reset/teardown lifecycle)
- **`ITestResource`** — Database, IHost, Docker container, etc.
- **`IHostResource`** — A resource that owns a DI container. Exposes `RootServices` (the host's
  root container) and `CurrentServices` (the per-scenario scope), and owns the scope itself via
  `BeginScenarioScope()`/`EndScenarioScope()`. `CurrentServices` **throws** outside a scenario —
  there is no silent root fallback.
- **`IRestartableResource`** — `Restart()` on a host resource (`HostResource`, `AlbaResource`,
  both generic forms) for specs whose subject is survival across a bounce: stop the application,
  start a fresh one over the *same* persistent state, keep the registration, and re-enter the
  scenario scope on the new container if one was open. Steps call it as `context.RestartHost(name)`.
  It is deliberately **not** `IRecyclableResource.Recycle` — recycle assumes the resource is
  broken and belongs to the supervisor between attempts; restart assumes it is healthy and is a
  step, mid-scenario, because the spec says so. A restart never runs `ResetBetweenScenarios`.
- **`AlbaContentRoot`** (`Bobcat.Alba`) — `AlbaResource<TProgram>` resolves the host's content
  root itself instead of trusting `WebApplicationFactory`, whose manifest lookup is relative to
  the *working directory* and whose `<solution>/<assembly>` fallback is wrong for anything under
  `src/` or `samples/`. Order: `TEST_CONTENTROOT_*` setting (left to the factory) → manifest in
  the test output → `[WebApplicationFactoryContentRoot]` → `<solution>/<name>` if it exists →
  `<name>.csproj` found below the solution → the test output directory. `resource.ContentRoot`
  says what was decided and why; `WithContentRoot` still overrides everything. See
  docs/sample-wiring.md footgun 2.
- **`AlbaResource` sets `JasperFxEnvironment.AutoStartHost = true` on `Start()`** (both forms;
  `AlbaResource.PrepareJasperFxHosting()` for a bare `AlbaHost.For<T>`). Required for any host
  whose `Main` ends in `RunJasperFxCommands` — every Critter Stack app — because under
  WebApplicationFactory the command runner otherwise parses the factory's synthesized
  `--environment/--contentRoot/--applicationName` flags and races the factory to start the host.
  It is a process-wide static that Bobcat never sets back, deliberately; see
  docs/sample-wiring.md footgun 15 for the trade-off and the JasperFx console chatter that is
  harmless (`Searching '…' for commands`, `cannot override the environment name`).
- **`AlbaResource<TProgram>.ConsoleLogLevel`** — default `Warning`: a filter rule scoped to the
  console logger provider that floors the hosted app's console output, because under MTP the
  console is the runner's and an ASP.NET host at `Information` writes several lines per request.
  A provider-scoped *rule*, not `SetMinimumLevel`, because an appsettings `"Default":
  "Information"` is itself a rule and rules beat the minimum level. Added before the user's
  `configure` so a user rule wins; other providers (debug, `BobcatLoggerProvider`) untouched;
  `WithConsoleLogLevel(null)` leaves the app alone. See docs/sample-wiring.md footgun 16.
- **`SetVerificationComparer`** — Static comparison utility called by generated code
- **`SuiteResults`** — Cross-feature aggregation with exit codes (0=pass, 1=regression fail, 2=catastrophic)

**`BobcatRunner.RunAll` never throws for a harness failure** (issue #123). A resource whose
`Start` throws, a global action whose `SetUp` throws, a `SpecCatastrophicException` from a
feature hook, or anything else that escapes the orchestration comes back as
`SuiteResults.CatastrophicFailure` (+ `CatastrophicException`), exit code 2, with every planned
scenario that has no result listed in `SuiteResults.NotRun` — by name, with the reason, and
deliberately *not* as a synthesized `ScenarioResult` (a scenario that never ran has no steps or
counts to report). `PreflightFailure` populates `NotRun` the same way. Before this, the
`SpecCatastrophicException` from `TestSuite.StartAll` escaped `RunAll`, the MTP host process
died with an unhandled exception (exit 134, nothing on the wire), and a supervisor could only
call that a crash. Two finer rules that fell out of it:

- **A `BeforeAll`/`AfterAll` that throws is a feature-level failure, not a suite one.**
  `FeatureResults.LifecycleFailure` is set, a `BeforeAll` failure puts the feature's scenarios
  in `NotRun`, the run exits 2 and **moves on to the next feature** — the feature-level analogue
  of a critical step aborting its scenario. `AfterAll` still runs when `BeforeAll` threw (the
  half-finished `BeforeAll` is the one that leaves something to clean up), so write it to
  tolerate that. A `SpecCatastrophicException` from either hook still stops the suite.
- **`TestSuite.DisposeAsync` disposes what `StartAll` started or tried to start**, in reverse
  order, every resource getting its turn before failures surface as one `AggregateException`.
  The resource that threw from `Start` is disposed too (a Docker resource may have its
  containers up and its health check failed); the ones after it were never asked to start and
  are not touched — their `DisposeAsync` was written assuming `Start` ran.

### Per-Scenario DI Scope
Each scenario runs as `ResetAll()` → `BeginScenarioAll()` → scenario → `EndScenarioAll()`. Persistent
state (DB rows, queues) is cleaned first, then a fresh DI scope is opened over it. Scope disposal
resets service *instances*; `ResetBetweenScenarios` resets *persistent* state — both matter.

Step/grammar parameter binding: `IStepContext` and any type a Gherkin cell can't produce are
resolved from the scenario scope; a name matching a data-table header wins over convention
injection. On a `[Table]` step (and a grammar's `Row`) whose text also carries Cucumber captures
(`these steps ran in {string}`), the captures bind positionally to the parameters no header
names — the same rule a non-table step uses — so a capture is never silently `default` (#122).
Overrides: `[FromScopedService]`, `[FromRootService]`, `[FromKeyedServices]` (all take
an optional `Resource` for multi-host suites), plus `[NewScope]` (child scope for one step) and
`[ScopePerRow]` (child scope per table row).

### Rendering (`src/Bobcat/Rendering/`)
- **`SpecRender`** — Intermediate model (feeds both Spectre.Console and future HTML)
- **`CommandLineRenderer`** — Consumes `SpecRender`, outputs Spectre.Console ANSI markup
- **`SpectreProgressObserver`** — `IExecutionObserver` for live progress during long tests
- Set verification renders as table with per-cell coloring (green/red/yellow)

### Three-Level Failure Semantics
1. **Assertion** — `[Check]` returns false → continue (gather all failures)
2. **Critical** — Exception in any step → abort scenario
3. **Catastrophic** — `SpecCatastrophicException` → stop entire suite

### Resilience (`src/Bobcat/Resilience/`)
The retry policy layer — `FailureLevel` promoted to a decision the *caller* of the executor acts
on. Framework-neutral by design: nothing here references Gherkin, so the same engine can drive a
`dotnet test` alternative over xUnit/tUnit (issue #41).

- **`Disposition`** — `Pass` / `FailAndContinue` / `RetryInProcess` / `RetryInFreshProcess` /
  `RetryAfterRecycle(resources…)` / `AbortRun`, each with a human-readable `Reason` that reaches
  the report.
- **`IFailurePolicy`** — `AttemptContext → Disposition?`. **Returning null abstains**, so narrow
  policies compose; `FailurePolicyChain` takes the first non-null and `DefaultFailurePolicy`
  always decides last. Register with `runner.AddFailurePolicy(...)`.
- **`RetryBudget`** — two independent ceilings because they fail differently:
  `MaxAttemptsPerTest` stops one pathological test, `MaxRetriesPerRun` stops a pathological
  *environment*. Default is `RetryBudget.None`, so an unconfigured run behaves exactly as before.
- **`ResilienceTags`** — projects Gherkin tags onto the trait dictionary policies read:
  `@retry(N)` → `Retry`, `@isolated` → `Isolated`, `@recycle(rabbit,kafka)` → `RecycleOnRetry`.
  Unrecognized tags pass through as `tag => "true"`. The #43 spike found traits are the only
  metadata channel that survives every front-end intact, which is why policy keys off **traits,
  not exception types** — tUnit erases exception types entirely on the MTP wire.

**Retries are opt-in.** An untagged failure is never retried, even with a budget configured.
Retrying by default would turn every genuine assertion failure into a slower one and make flaky
indistinguishable from broken.

**Honest reporting ships with it** — `RunOutcome` is `CleanPass` / `PassOnRetry` / `Failed` /
`Aborted`, and `PassOnRetry` is never folded into `CleanPass`. `SuiteResults.PassedOnRetry` is
the run's flakiness ledger; a pass-on-retry still exits 0 but is reported separately. Retry
dispositions the runner cannot yet honour (`RetryInFreshProcess`, `RetryAfterRecycle` — both need
the supervisor) are recorded in `UnsupportedDispositions` rather than silently downgraded, so a
report never implies a retry that never happened.

Every attempt gets the full `ResetAll` → `BeginScenarioAll` → `EndScenarioAll` bracket; a retry
reusing dirty state would be testing something other than the scenario.

#### Recovery hints — author-declared, per failure class (issue #44 layer 1)

A tag says *this test* is unreliable; a hint says **which failure** is unreliable and what fixes
it. Declared on the fixture, so an assertion failure on the same scenario is still reported as
the bug it is:

```csharp
[ClearsOnRetry(typeof(TimeoutException), Because = "the broker is slow to warm up")]
[ClearsOnRecycle("rabbit,kafka", typeof(BrokerUnavailableException))]
[ClearsInFreshProcess(typeof(BadImageFormatException))]
[NeverRecovers(typeof(SomeDeterministicBug), Because = "this is a real bug")]
public class OrderFixture : Fixture;
```

- **The attributes and `DispositionKind` live in JasperFx, not Bobcat** — namespace
  `JasperFx.Testing`, from **JasperFx 2.37.0** (issue #63). Any suite already referencing JasperFx,
  directly or through Marten/Wolverine/Polecat, can annotate itself **without referencing Bobcat**.
  That mattered because `docs/parallel-ready-suites.md` Step 0 sells "a test project needs no
  reference to Bobcat", and shipping the attributes as Bobcat types would quietly have spent it.
  One enum rather than a Bobcat copy mapped at a seam: two enums meaning the same thing is how the
  vocabulary drifts, and the drift would stay invisible until a hint silently stopped matching.
  Everything that *acts* on a hint stays here — `RecoveryHint`, `RecoveryHintSet.Best`,
  `HintedFailurePolicy`, the scope rule.
  - Consequence worth knowing: **a project using `DispositionKind` must reference `JasperFx`
    directly.** `Bobcat.Console` did not, and picked up 2.36.1 transitively from `WolverineFx.Http`
    — Central Package Management only pins what a project actually references, so the central
    2.37.0 did not apply and it compiled against a JasperFx nobody chose.
  - `ClearsOnRecycle` parses its resource list independently of `ResilienceTags.ParseResources`,
    because JasperFx must not reference Bobcat. `ResourceParsingAgreementTests` pins the two
    together; without it they can diverge silently and a tag and a hint naming the same brokers
    would resolve to different names.
- **`HintedFailurePolicy` sits between the user's policies and `DefaultFailurePolicy`** — explicit
  code still wins, and any failure no hint describes falls through to the tag-driven default
  unchanged. A run with no hints behaves exactly as it did before.
- **A hint never widens the budget.** `RetryBudget.MaxAttemptsPerTest` is the operator's ceiling;
  an unconfigured run still retries nothing however many hints are declared. Knowledge of what
  recovers belongs to the test's author, how much time the run may spend belongs to whoever runs
  it, and conflating them would let a spec author escape the ceiling.
- **`NeverRecovers` is the counterweight.** Without it the only way to stop a broad `@retry(3)`
  re-running a deterministic bug is to remove the tag, which also stops the retries that were
  pulling their weight.
- **`FailureSignature` is the matching key** — type *names*, not `Type`, because out of process a
  name is all there ever is. In process it holds the whole inheritance chain, so a hint on a base
  class matches a derived failure; from the MTP wire it holds one name, so a base-class hint
  abstains. That asymmetry is deliberate and degrades to "no hint applied" — never to a wrong
  retry. A worker that erases the type entirely (tUnit) simply matches nothing.
- **Hints do not cross the process boundary as attributes.** The supervisor drives the worker as a
  process and never loads its assembly, so `Supervisor.RecoveryHints` takes `RecoveryHint` records
  directly. Projecting a worker's own hints across the wire is the follow-on. This is why
  `RecoveryHint` exists separately from `RecoveryHintAttribute` — it is also the shape the layer-2
  ledger will produce.
- **Scope beats type specificity.** Assembly < fixture < test, and the narrowest declaration wins
  even when it names a less specific type, so a fixture can override a run-wide default without
  knowing what that default names. Within one scope, the hint naming the exact type wins.
- **The hint travels on the `Disposition`**, not just inside its `Reason` prose, because the case
  most needing a report is a hint that *suppressed* a retry — otherwise a tagged test that failed
  once looks like its tag stopped working. Surfaced as `↯ recovery hint applied` in the console
  and as a `hint` object per attempt in `RunReport.ToJson`.

**Layer 2, the committed ledger, is built** (`src/Bobcat/Ledger/TestLedger.cs`, design of
record `docs/ledger-design.md`) — one store serving #44's hint proposals, #142's duration
trends, and `Supervisor.KnownTestDurations` (the third consumer that made it infrastructure:
`ledger.KnownDurations()` feeds `WorkPlan`'s balancer from the first pass). The open questions
were settled by making the merge strategy the design: a grow-only set of per-(test, run)
observations plus a deterministic clock-free prune — `Record`/`Merge` commutative, associative,
idempotent; serialization canonical (byte-identical for the same observations, whoever folds) —
so a git conflict always resolves by load-both-sides → `Merge` → save, no artifacts needed.
Failure classes are keyed by type *name* (the `FailureSignature` rule); aging is newest
`MaxRunsPerTest` per test plus explicit `PruneTestsNotSeenSince(cutoff)` for deleted tests.
Feeds: `SupervisorLedger.From(SupervisorResults, runId, at)` (rich — every attempt survives) and
`LedgerRuns.From(SuiteResults, runId, at)` (in-process; the #141 wall clock is the duration, and
a pass-on-retry's original failure type is honestly unknown). Nothing writes the ledger
implicitly — recording is the caller's line of code, and stall-induced entries (#173) never
feed hint evidence. The DECIDED fork stands, now in code: `ProposeHints()` emits attribute
*text* with `Because` evidence (plus the `[NeverRecovers]` counterweight, `minOccurrences`
gating both) — a human accepts by writing it into the code, and nothing reads proposals back
into an `IFailurePolicy`; a policy that silently learns "just retry this" is exactly how red
gets laundered into green with nobody deciding to.

### MTP Host (`src/Bobcat.Mtp/`)
Bobcat's spec runner exposed as a **Microsoft.Testing.Platform test host**, so an IDE Test
Explorer, CI, or a future Bobcat supervisor sees scenarios as ordinary tests. This is the
"expose" half of the #41 seam; the "drive" half (supervisor) is still to come.

```csharp
public static class SpecsRunner
{
    public static Task<int> Main(string[] args)
        => BobcatTestApplication.Run(args, runner =>
        {
            runner.ScanForFeatures(typeof(SpecsRunner).Assembly);
            runner.Suite.AddResource(new AlbaResource<Program>());
        });
}
```

- **One scenario = one MTP test node.** Features are not nodes — they are a grouping concept
  owning `BeforeAll`/`AfterAll`, and making them nodes would imply the platform could schedule
  them independently.
- **`SpecNodeMapping`** is pure and static so the projection is testable without launching a
  host. Uid is `"{Feature}/{Scenario}"` — deliberately **the same string `BobcatRunner` uses as
  the retry budget's test id**, so one scenario has one identity everywhere.
- **`failed` vs `error` is honoured** (a comparison disagreed vs an exception escaped), because
  that split is what a supervisor's `Disposition` policy keys off.
- Tags travel as MTP traits via `ResilienceTags`, so `@isolated` / `@recycle(...)` are readable
  by a supervisor that knows nothing about Gherkin.
- MTP has no "passed on retry" state, so `RunOutcome` travels as `bobcat.outcome` /
  `bobcat.attempts` metadata rather than being collapsed into a clean pass.
- Discovery **never starts resources** — IDEs discover on every build.
- **A scenario the run planned and could not execute is reported as a node in `error`**, with
  the harness exception (a `ScenarioNotRunException` wrapping the real cause) and
  `bobcat.outcome = NotRun` (issue #123). `PublishingObserver.RunFinished` publishes one per
  `SuiteResults.NotRun` entry, and `BobcatTestFramework.run` has a last-resort catch that does
  the same for anything still unreported should `RunAll` ever throw — so the host process never
  dies of a harness failure. `error` rather than `skipped`, because a supervisor counts skipped
  as succeeded and a suite whose broker never came up must not read as green; and rather than
  publishing nothing, because a run with no verdicts is exactly what a crashed worker looks
  like. The distinction from a real crash is structural — every planned node gets a verdict
  and the process exits normally — which is why `Bobcat.Supervisor.Tests` asserts a
  start-failure run has **no `Indeterminate` and no `WorkerFaults`**, just `Error` outcomes
  naming the resource.

**`dotnet test` works** — it needs `Microsoft.Testing.Platform.MSBuild`, which `Bobcat.Mtp`
depends on, so a consumer gets it transitively. That package also supplies the `ProjectCapability`
Visual Studio and VS Code Test Explorers look for. It normally synthesizes a `Main`, which would
be a second entry point beside the consumer's own `BobcatTestApplication.Run` (CS0017), so
`Bobcat.Mtp` ships `buildTransitive/Bobcat.Mtp.props` turning that off. A `ProjectReference` does
not consume another project's build assets, so in-repo projects must set
`GenerateTestingPlatformEntryPoint=false` themselves — `Bobcat.Mtp.SampleHost` does.

Turning that entry point off also drops the generated `AddSelfRegisteredExtensions`, which is
where the **MSBuild extension** (the thing `dotnet test` actually talks to, via
`--internal-msbuild-node`) would have been registered. `BobcatTestApplication.Run` therefore
registers it itself (`TestingPlatformBuilderHook.AddExtensions`). Until `Bobcat.Console.Specs`
no Bobcat host in the repo had `IsTestProject=true`, so `dotnet test` had never actually
collected one and the missing registration went unnoticed — running the executable directly
never exercises that path.

The host is also runnable directly (`./MySpecs`, `--list-tests`, `--filter-uid <uid>`), which is
what `Bobcat.Mtp.Tests` exercises.

`Bobcat.Mtp.SampleHost` is a spec project run as a host; `IsTestProject=false` keeps `dotnet
test` from collecting its deliberately-failing scenarios, and `Bobcat.Mtp.Tests` launches it as
an executable instead.

### Supervisor (`src/Bobcat.Supervisor/`)
The out-of-process half of #41: runs a suite across **worker processes** and applies the
resilience policy at the only altitude that can act on it. `RetryInFreshProcess` and running an
`[Isolated]` test alone cannot be decided or performed by the thing running inside the process
that needs replacing.

```csharp
var supervisor = new Supervisor(new MtpWorkerFactory("path/to/MySpecs"))
{
    RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 }
};
var results = await supervisor.Run();   // results.ExitCode, results.PassedOnRetry, …
```

- **`IWorkerClient` is the seam.** Every wire detail — JSON-RPC framing, MTP node shapes,
  parameter names — lives behind it. That was the #43 spike's top-listed mitigation: MTP's server
  mode is undocumented and has moved between versions, so if it shifts (or we ever want the
  native-protocol fallback) it is one implementation, not a refactor.
- **No `Microsoft.Testing.Platform` dependency.** The supervisor speaks the wire protocol rather
  than hosting the platform. That is part of what lets it drive xUnit v3 and tUnit workers as
  readily as Bobcat's own — there is a test that drives `Bobcat.Tests` (an xUnit v3 host) to prove
  it, not just assert it.
- **Scheduling:** isolated tests are identified from **discovery traits, before anything runs**,
  and each gets its own process. Everything else is spread across `MaxParallelWorkers` lanes (one
  by default). Discovery itself uses a throwaway worker so it neither inherits nor leaves state.

#### Parallel worker pool (`WorkPlan`)

`MaxParallelWorkers` defaults to **1**, so a run is sequential unless asked otherwise — parallelism
changes what a suite means, and turning it on by default would convert working suites into flaky
ones on upgrade. Same reasoning as retries being opt-in.

- **Partitioning is by test class, and that is a correctness rule, not a tuning knob.** Measured on
  Wolverine's `PersistenceTests`: splitting the same 78 tests across 4 workers *per test* failed
  1–4 non-deterministically, while splitting *per class* passed 78/78 at the same wall clock. A
  test class is the unit every framework's isolation contract is written against — fixtures,
  `IAsyncLifetime`, and any static state. The real example that made this concrete was a class
  whose setup read `var schemaName = "sqlserver" + ++count;` from a `static int`: split across four
  processes, each restarts at zero and all four collide. **Nothing about that is visible in the
  test list**, so the planner must never separate tests the author kept together.
- **`WorkPlan.ClassOf` derives the key from the display name** because that is the only structural
  naming signal every front-end supplies. It handles Bobcat's `Feature/Scenario` and the dotted
  `Namespace.Class.method`, stripping theory arguments first so `method(x: "a.b")` does not shatter
  into a partition per argument. `Supervisor.PartitionKey` overrides it when the real coupling is
  something else.
- **Balancing is longest-processing-time-first over whole partitions**, fed by
  `Supervisor.KnownTestDurations` (per-test, by uid). A first run has none and falls back to test
  count; tests missing from partial data are charged the **median** of what is known, so adding a
  test to a suite of 30-second integration tests is not costed at a nominal second.
- **The largest partition sets the floor.** Wall clock is the slowest lane, so no fleet size beats
  the slowest single class. Measured: 164s baseline → 73.2s at 4 workers (count-balanced) → 70.3s
  duration-balanced → **66.8s at 8 workers**. Doubling the fleet bought 3.5s because one 61-second
  test dominates. That is why issue #56 (find the hot spots) and this feature are the same
  bottleneck from two directions.
- **A same-process retry returns to the lane the test ran in**, not merely to some warm worker.
  A class's static state lives in the process that ran it, so retrying elsewhere would be a
  fresh-process retry wearing a same-process label.
- **Recording is serial and in lane order** even though lanes finish in whatever order the OS
  decides — a report must not depend on which worker won the race.
- **What it cannot protect against** is state shared *between* classes: a fixed port, or one
  database every class truncates. That needs per-worker environments (`MtpWorkerFactory.EnvironmentFor`), which is
  still unbuilt. `PersistenceTests` did not need it; running several *projects* concurrently will.
- **`GuardAgainstAnUnfilteredRun` is not optional.** MTP silently ignores an unrecognised subset
  parameter and runs the whole suite, which is indistinguishable from a filter matching
  everything. A retry that did that would fold unrelated failures into the attempt, so a
  mismatch is treated as a protocol fault rather than trusted.
- **A crashed worker yields `Indeterminate`, never "failed".** The spike measured 0-of-9 outcomes
  surviving a crash on some hosts, so silence is absence of evidence. The converse holds too: a
  worker whose *harness* failed — a resource that would not start — reports every planned test
  as `error` with the reason and exits normally (see the MTP host note on issue #123), so it is an
  ordinary red run with a message, not a crash. Indeterminate maps to exit
  code **2**, not 1 — "we don't know what happened" is not an ordinary red build. Every
  indeterminate outcome carries the worker's **exit code and last standard error** (bounded to
  the final 20 lines) in its `ErrorMessage`, and the run collects them in
  `SupervisorResults.WorkerFaults`. An indeterminate count with no explanation is not a report.
- **`RetryAfterRecycle` recycles, then runs the test alone in a fresh process.** Reusing the
  shared worker would leave it connected to the broker we just discarded. Naming a resource
  nobody registered is reported as a wiring mistake, not silently retried without recycling. A
  recycle that *fails* aborts the run with `AbortReason` rather than throwing, so results
  gathered before the infrastructure broke survive.
- **`ISupervisorObserver` narrates the run live** (`Supervisor.AddObserver`, issue #84):
  every attempt with the verdict that followed it, scheduled retries, lane start/finish,
  recycles, worker faults. Default no-op members, and an observer that throws is logged and
  stepped over — a dashboard must not be able to fail a test run. A scheduled retry carries the
  **true attempt number**, which is the fact only the supervisor has: a worker counts from one,
  because its tracking belongs to a `BobcatRunner` and the MTP host builds a fresh one per run
  request. `WorkerFaulted` has a structured form (`WorkerFault`: description, exit code, stderr
  tail, lane or null for a one-test process, pid) whose default forwards to the string one, so
  either signature works for an observer. All of it reaches the monitor as wire events
  (`LaneStarted`/`LaneFinished`/`ResourceRecycled`/`WorkerFaulted`) via `SupervisorRunPublisher`;
  see `docs/monitor-design.md` Bobcat-side seams item 4 for what the dashboard folds from them.
- **The worker's pid is surfaced, never guessed at** (issue #146): `IWorkerClient.ProcessId`
  (default interface member, null for an in-process client), stamped onto
  `WorkerLaunchContext.ProcessId` by `launchWorker` so `TestUpdated` carries it, announced by
  the `WorkerStarted` observer callback (fired for **every** launch, discovery included — a
  discovery worker never reports progress but is still a process someone may need to diagnose),
  and riding on `WorkerRunResult.ProcessId` into `WorkerFault`. The enabler for pointing
  external diagnostics — a dump, a stack capture, an RSS sample — at the right process: the
  Wolverine watchdog that had to infer the pid from `/proc` latched onto `sqlservr` instead of
  the test host, which is the class of bug this removes (issues #147/#149 build on it).
- **Stall detection and the heartbeat are reporting, never action** (issues #145/#148, one PR
  because they are the same bookkeeping): `InFlightLedger` is the supervisor's live in-flight
  table — fed from the `TestUpdated` stream before observers are notified, read by one run
  ticker (`StallCheckPeriod` = 1s when stalls are configured). `StallThreshold` /
  `StallThresholdFor` (per-test, wins for discovered tests) fire the `TestStalled` observer
  callback and a `STALLED:` log line **once per attempt** — a retry resets the clock — and
  collect on `SupervisorResults.StalledTests`, which survives into `RunReport` even for a green
  run (same reasoning as `Quarantine`). `HeartbeatInterval` emits one `Log` line however many
  lanes are running plus `Heartbeat(SupervisorHeartbeat)`; the "longest running" clause is the
  point — a stuck run shows as that figure climbing before any threshold fires. All off by
  default; whether the supervisor then acts on a stall is the second, separable, opt-in
  decision — `StallAction`, issue #173, below. Time is injectable (`Supervisor.Time`, a
  `TimeProvider`) so `StallAndHeartbeatTests` drives a fake clock and holds "hung" workers on
  a `TaskCompletionSource` instead of sleeping.
- **Acting on a stall is `Supervisor.StallAction` (issue #173)** — default `Report` (#145's
  behaviour, unchanged; detection always still reports whatever the action). `KillAndRetry`:
  the ticker's `escalate` kills the stalled worker through the new `IWorkerClient.Kill(reason)`
  (on `MtpWorkerClient`: straight to the process-tree kill through #147's `OnBeforeKill` hook
  with the stall's reason — no polite exit, because asking a wedged process nicely is how a
  kill hangs; connection teardown stays with the later `DisposeAsync`). The killed worker's
  `Run` returns a fault with synthesized Indeterminate outcomes exactly like a crash, and
  `record()` intercepts them (`stallKilled` flag, consumed per lane / per solo worker): the
  stalled test gets `RetryInFreshProcess` once — a second stall on its solo retry is
  `FailAndContinue`, never retried forever — and **innocent batch-mates killed alongside it
  resume in their lane** (`RetryInProcess` → the same slot, necessarily a fresh process)
  instead of being reported Indeterminate for the supervisor's own act. `AbortRun` stops the
  whole run on the first stall; in `KillAndRetry`, `MaxStallKills` (default 3) is the run-wide
  ceiling after which the next stall aborts — repeated stalls across tests are the shape of
  dead infrastructure. Three ledger rules, all deliberate: stall-managed outcomes **never
  consult the policy or spend the `RetryBudget`** (the kill is the supervisor's own act; a
  wedge is not a flake); `SupervisorAttempt.StallInduced` keeps those attempts out of
  `PassedOnRetry`/`Quarantine` (via `TestReport.WasRetriedForFailure` — a pass whose only
  earlier attempt was stall-induced is a **clean pass**, the stall story lives on
  `SupervisorResults.StallKills` + `StalledTests`); and a stall kill is **not a
  `WorkerFault`** — the fault ledger is for deaths the supervisor didn't order. Observers get
  `StallKilled(StallKill)`; `RunReport` renders a "Workers killed to clear a stall" section
  and a `stallKills` JSON block. Not on the monitor wire as its own event yet — the stall
  (`test_stalled`) and the retry (with the stall reason) already travel; a dedicated
  `stall_killed` wire event is a follow-on if the dashboard wants to render kills distinctly.
- **`MtpWorkerFactory.OnBeforeKill` offers a live worker to the consumer before killing it**
  (issue #147) — a seam, not a feature: Bobcat ships no dump logic and takes no dotnet-dump
  dependency; the consumer captures what it wants (`dotnet-dump collect` + `dumpasync` is what
  diagnosed wolverine#4100) against the `WorkerKillContext` pid, under `BeforeKillTimeout`
  (default 30s). Fires **only for a live process about to be forcibly killed** — one that was
  asked to exit and did not (a wedged worker, after `DisposeAsync`'s 5s grace) or one that
  launched but never became usable; a healthy worker exits when asked and never reaches it, a
  crashed one already has `WorkerFault`. `MtpWorkerClient.InvokeBounded` wraps the hook in
  `Task.Run` so even a synchronously-blocking hook cannot hold the kill past the deadline —
  this is the one callback a run genuinely waits on, which is why the ceiling is explicit.
  The sample worker's `Basics/hangs when armed` (`BOBCAT_HANG`) is the reusable wedge.
- **`ResourceSampleInterval` is RunTiming for RSS** (issue #149): `IWorkerClient.SampleWorkingSet()`
  (default null; `MtpWorkerClient` reads `Process.WorkingSet64` — no new dependency, null once
  the process is gone), `MemorySampler` brackets every attempt with boundary samples off the
  test-update stream and adds interval peaks via the run ticker (which serves stalls, heartbeat
  and sampling from one timer at the min cadence). Per-attempt "retained" deltas are attributed
  **only when the attempt had its process to itself for its whole window** — a second test
  entering poisons everyone, including itself, and those attempts report a null delta
  (unattributed, counted) rather than a guess. Unmeasured produces no record at all, never
  zeroes; JSON figures are null. `RunResources.For(results)` is the reporting view (peak per
  worker, top-10 retainers) rendered by `RunReport`; raw figures live on
  `SupervisorResults.WorkerMemory`/`TestMemory`. Off by default, report-don't-act — same
  guardrail as `RunTiming`.
- **`Supervisor.Snapshot()` is what a cancelled run leaves behind** (issue #150). `Run(ct)`
  still throws on cancellation — that contract deliberately did not change — and a capped CI
  job is exactly that case, with GitHub discarding the cancelled job's logs on top. The
  consumer wires the CI termination signal (GitHub Actions sends SIGTERM with a real grace
  period) to `Snapshot()` and writes its flakiness ledger from the result, which is stamped
  `SupervisorResults.IsPartial` (`Summarize()` leads with `PARTIAL`, JSON carries `partial`).
  Three tiers of honesty for a test without a recorded verdict: a verdict **heard on the live
  update stream** whose lane never returned is kept (results are recorded per-lane, so a
  single-lane snapshot would otherwise call nearly the whole batch indeterminate —
  `InFlightLedger.ProvisionalVerdicts` fills the gap, minus the detail only a recorded attempt
  has); a test in flight is `Indeterminate` saying so with its duration and lane; a test never
  reached is `Indeterminate` saying that. Recording state sits behind `_recordGate` so a
  snapshot from another thread reads a consistent view; note the long-standing classification
  that Indeterminate tests also count in `Failed` still holds for snapshots.
- **The cluster's live surfaces are on the monitor wire** (2026-08-24): `worker_started`
  (never for discovery — it launches before the run bracket opens, and `run_started` stays the
  stream's first event), `test_stalled`, and `run_progress` (the #148 heartbeat, carrying peak
  worker RSS when sampling is on — distinct from `run_heartbeat`, the bare liveness ping).
  Posted by `SupervisorRunPublisher`, folded into `RunProjection` and the Pinia runs-store
  under mirrored rules, read back via `GET /api/runs/{id}` and MCP `run_status`, rendered by
  `SupervisorTopology`. Details in `docs/monitor-design.md` Bobcat-side seams item 4.
- **Preflight runs once before any worker is launched** (`Supervisor.Preflight`), and in-process
  before any feature (`BobcatRunner.Preflight`). See the environment-check note below.
- **Reporting** lives in `RunReport.ToText` / `RunReport.ToJson`. `Quarantine` is every test that
  needed more than one attempt — membership is *"was retried"*, not *"eventually failed"*,
  because a green build is exactly when chronic flakiness would otherwise go unnoticed.
- **Where the run spent its time** is `RunTiming` (issue #56 layer 1) — pure computation over
  `SupervisorResults`, rendered by `RunReport` into a `Timing` section and a `timing` JSON block.
  It answers what a profiler cannot, because these are properties of the *run* rather than of a
  process: slowest-N with each test's **share of wall clock** (the percentage is what makes
  someone act — "one test is 35% of the run", not "60.9s"), parallel efficiency
  `sum(durations) / wall clock` (Wolverine's `PersistenceTests` measured **1.07x**, which is how
  we learned its collection fixtures serialize xUnit's in-process parallelism), retry-amplified
  cost, the price of isolation, and worker launch overhead.
  - **Report, don't act** — the same guardrail as #44's hints. Failing a build on a duration
    threshold turns a useful signal into a flaky one; whether a slow test is a bug or a genuinely
    slow integration test is a judgement, and this is the evidence for it.
  - **Unmeasured is never zero-filled.** A framework that erases durations on the wire (tUnit,
    same as exception types) yields `Unmeasured` counts and `null` JSON figures, and the text
    report says the numbers are a floor. Zero-filling would make every other figure quietly wrong.
  - Layer 3 (trend across runs) now rides the committed ledger — `TestLedger.Trends()`, see
    `docs/ledger-design.md`. Layer 2 (sleep-shaped-duration heuristics) stays **not built**:
    the cross-run variance it needs exists now, but its false-positive question is open and
    `[SlowByDesign]` belongs in `JasperFx.Testing` per the #63 precedent.

`Bobcat.Supervisor.SampleWorker`'s scenarios can *detect* whether they were isolated (a static
per-process counter), so the isolation tests prove isolation happened rather than merely proving
a process was launched. There is a control test showing the same scenario fails when batched.

#### Environment checks reuse JasperFx — there is no Bobcat `IEnvironmentCheck`

**Do not define one.** Issue #41's text sketched a new `IEnvironmentCheck` interface; that was
written before checking, and JasperFx already owns the concept. Note also that the *old Oakton*
`IEnvironmentCheck` interface no longer exists in JasperFx 2.x — what exists is:

- `JasperFx.Environment.EnvironmentCheckResults` — `RegisterSuccess` / `RegisterFailure` /
  `Succeeded()` / `Assert()`
- `JasperFx.Environment.EnvironmentChecker.ExecuteAllEnvironmentChecks(IServiceProvider, ct)`,
  which collects checks from `ISystemPart.AssertEnvironmentAsync`,
  `JasperFx.Resources.IStatefulResource.Check`, and Microsoft's `IHealthCheck`
- `EnvironmentCheckException`, `LambdaCheck`

So `Bobcat.Runtime.Preflight` is a **thin collector that returns `EnvironmentCheckResults`**, and
`ITestResource.Check(CancellationToken)` is a default interface method deliberately mirroring
`IStatefulResource.Check` — same verb, same "throw to fail" contract — so one resource satisfies
both without adapting. `Preflight.AddContainerChecks(...)` delegates straight to
`EnvironmentChecker`. A Critter Stack user must never have to write their checks twice.

#### Decided against: an `ITestHostLauncher`-style abstraction

**Do not add a separate launcher seam between the supervisor and the worker process.** Process
creation stays inside `MtpWorkerClient.Launch`, and `IWorkerFactory` remains the only injection
point. Revisit only if a concrete requirement below actually arrives.

The idea was to port VSTest's `ITestHostLauncher` — let a caller supply the process. Rejected
because:

- **Its main purpose does not apply.** VSTest's launcher exists chiefly so an IDE can start the
  host itself and attach a debugger. In MTP that is a *protocol* concern, not a launch one: the
  client advertises `capabilities.testing.debuggerProvider` during `initialize` and the host then
  sends `client/attachDebugger`. We currently pass `false`. Building a launcher to solve
  debugging would solve it in the wrong layer.
- **The real gap it would have carried is now fixed directly** — worker exit code and standard
  error are captured and reported (see above). That was the concrete defect; it did not need an
  abstraction.
- **`IWorkerFactory` already covers the seam we use.** `FakeWorkerFactory` in the tests proves it,
  and a second interface would be ceremony.

The remaining motivations are speculative: per-worker environment shaping (each isolated worker
pointed at its own broker/schema), and container or remote workers.

**Resolved — and the deferral paid off.** Per-worker environments became real the moment parallel
workers had to point at their own database. The requirement arrived far smaller than the
abstraction we would have built for it: `IWorkerFactory.Launch` gained a
`WorkerLaunchContext(Lane, Purpose)` parameter and `MtpWorkerFactory` an `EnvironmentFor` hook.
No launcher interface, no second seam. Had we written `IWorkerLauncher` up front we would have
shipped a whole abstraction where a parameter was wanted.

- `Lane` is bounded by `MaxParallelWorkers` and is the slot to key per-worker resources off — a
  database name, a schema prefix, a port. Discovery, isolated and recycled launches all report
  lane 0, because they never run while the pool is running, so the number of databases a suite
  provisions equals the workers it asked for rather than the processes it happened to start.
- `Purpose` distinguishes those cases when a caller wants to (a throwaway database for discovery).
- Per-worker environment layers **over** the factory's shared environment, and the lane's value
  wins — shared is a baseline, not a ceiling.

Container or remote workers remain speculative. Same rule applies: build it when something needs it.

### Bobcat.CritterStack is store-agnostic (`src/Bobcat.CritterStack/`)

Decision of record 2026-08-20 (issue #103): Bobcat's event-sourcing helpers bind to the
**`JasperFx.Events` abstractions**, never to Marten — the same discipline `Wolverine.CritterWatch`
lives by — so one package serves Marten, Polecat and Fisher. `Bobcat.CritterStack` references
`Bobcat`, `Bobcat.Wolverine` and the `JasperFx.Events` package; **it does not reference
`Bobcat.Marten`, Marten, Polecat or Fisher**, and a spec project using it needs none of those
either (`samples/BankAccountES/Tests` has no `using Marten`). `Bobcat.Marten` stays as the
*document-store* flavour — `MartenResource`, `[MartenEntities]`, `QueryByIdAsync` — not as the way
to reach the event store.

- **The store comes from the host's container, not from a Bobcat resource type.** Marten
  (`AddMarten`), Polecat (`AddPolecat`) and Fisher (`AddFisher`) all register their store as
  `JasperFx.Events.IEventStore`; `context.EventStore()` / `services.EventStore()` resolve it from
  the `IHostResource`'s `RootServices`. `storeName` (matched on `IEventStore.Identity.Name`)
  disambiguates a host with several stores.
- **Everything the abstractions cover goes through them.** Streams are read via
  `IEventStore.OpenReadOnlyEventStore()`; projection waits via
  `IEventDatabase.WaitForNonStaleProjectionDataAsync` (the same wait JasperFx's
  `ProjectionScenario` performs) and `AllProjectionProgress`, so it does not matter who runs the
  daemon — Marten's `IProjectionCoordinator`, Fisher's hosted service, or Wolverine. A projection
  wait decides *what* to wait on from the store's configured shards (`IEventStore<,>.AllShards()`),
  not from the progress table: a daemon writes a shard's row only after its first batch, so right
  after the first append an empty table is indistinguishable from "no async projections" and a wait
  keyed on it passes vacuously. A name matching no configured shard throws and lists them.
- **Two gaps in JasperFx.Events 2.53.0 are bridged by convention, and say so.** There is no
  abstraction for *aggregating* a stream or for *wiping* a store. `EventStores.AggregateStreamAsync`
  uses the read-only view when it is an `IQueryEventStore` (Marten, Polecat) and otherwise opens a
  session through the `IEventStore<TOps,TQuery>` closure and finds its `Events` member (Fisher).
  `EventStores.ResetAllDataAsync` follows `Advanced.ResetAllData(ct)` / `ResetAllDataAsync(ct)` /
  `Advanced.Clean.DeleteAll*` — the shape all three stores share — and a store matching none gets
  an exception naming what was looked for. JasperFx's own `IStatefulResource.ClearState` is *not*
  enough: Marten's database does not implement `IDatabaseWithRewindableState`, so
  `jasperfx resources clear` is a no-op for it (`ClearStatefulResourcesAsync` is still offered, for
  Wolverine's envelope storage). Both are the same bounded, documented softening as
  `GrammarBehaviors.Resolve`; when the abstraction lands upstream, the convention path is what gets
  deleted.
- **`ProjectionScenario<,>` is not what the waits delegate to, and that is a decision, not a gap
  (2026-08-21, JasperFx.Events 2.53.0).** Checked on the bump: it is a *scripted* harness — it
  wipes data (`DeleteExistingData` defaults to true), builds and owns its **own** daemon, appends
  through its **own** session, and is reached only through each store's `Advanced`
  (`EventProjectionScenario` on Marten and Polecat, `EventProjectionScenarioAsync` on Fisher), each
  closed over that store's session pair with no non-generic interface and no `IEventStore`
  accessor. A Bobcat spec appends through the application's own handlers and waits on the host's
  daemon, so the right tool is what the scenario itself calls after each batch —
  `IEventDatabase.WaitForNonStaleProjectionDataAsync` / `IProjectionDaemon.WaitForNonStaleData` —
  which is what `EventStores` already does. A "given these events, then this document" grammar is
  where a scenario would earn its place; that is #104's grammar modules, and it would have to be
  store-specific or reflective. Nothing in 2.53.0 retired a convention path either: there is still
  no aggregate-stream member on `IReadOnlyEventStore` (Fisher's view is not an `IQueryEventStore`,
  so `AggregateStreamAsync` still opens a session through the `IEventStore<,>` closure — proved on
  the real store) and still no reset abstraction (`Advanced.ResetAllDataAsync` is Fisher's spelling
  — also proved). The `IProjectionCoordinator` fallback stays for a store that does not override
  `IEventStore.AllDatabases()`; all three do.
- **Fisher is the inner-loop target, and is covered.** The aligned set landed 2026-08-21 (issue
  #125): WolverineFx 6.29.1 ↔ Marten 9.28.0 ↔ JasperFx 2.53.0 ↔ Fisher 1.0.2 ↔ Polecat 5.19.2.
  `Bobcat.CritterStack.Tests` runs the same five integration tests against Marten (Postgres 5445,
  `[PostgresFact]`) and Fisher (`FisherIntegrationTests`, a temp SQLite file, never skipped). The
  Fisher host needs `ApplyAllDatabaseChangesOnStartup()` registered *before* `AddAsyncDaemon` —
  Fisher builds its schema lazily and the daemon reads the progression table on start
  (`docs/sample-wiring.md` footgun 13). Polecat needs SQL Server and is a documented manual run:
  `AddPolecat(...)` registers `IEventStore` like the others and its `ProjectionScenario` and reset
  share Marten's spellings, so the same code path applies.
- **Acceptance (#103): `samples/BankAccountES` runs on Marten and on Fisher — 9/9 on each — with
  no `Bobcat.Marten` reference, switched by `EventStore=Marten|Fisher` in configuration.** The
  host was rewritten to the store-agnostic vocabulary (`[DeciderFunction]`, `[Entity]`,
  `Storage.StartStream`, `IEventStoreOperations`, `IDocumentReadOperations`, a self-aggregating
  `Snapshot<T>` read model in place of a `SingleStreamProjection<,>` subclass) so that
  `Program.cs` is the only file naming a store; the swap table is footgun 14. The Fisher leg runs
  in CI (`samples.yml`), because a SQLite file needs nothing the runner does not have — which is
  the whole argument for Fisher as the inner loop.

#### `CritterStackFixture` + shipped grammar modules (issue #104)

`Bobcat.CritterStack` ships the slice-declaring Gherkin vocabulary as a base fixture. Derive from
`CritterStackFixture` and every event-sourcing step is bound with no further code — that is the
canonical route, riding the base-class discovery above; `[IncludeGrammars(typeof(CritterStackGrammars))]`
is the mix-in route (`CritterStackGrammars` is an empty `sealed` subclass whose steps the module path
discovers through its base).

- **Typed steps** (shared with the code-first API, #105) sit on the fixture: `GivenEvents<T>(id,
  events)` / `GivenNoEvents<T>(id)`, `WhenCommand<T>(command)` (Wolverine invoke + `TrackedSession`,
  returns the `AggregateExecution`), `ThenEvents(...)`, `ThenNoEvents()`, `ThenValidationFails(string)`,
  `ThenCommandRefused()`, `ThenDocument<T>(id, assert)`, `ThenMessagesSent<T>()`.
- **Grammar steps** wrap those: `Given no events for {aggregate} "{id}"` · `Given events for
  {aggregate}` + table (an `Event` column names each row's type, the rest are its fields) · `When
  {command} is received` + table (binds the command record) · `Then {event} is emitted` (+ optional
  table) · `Then no events are emitted` · `Then validation fails with {string}` · `Then the command
  is refused` · `Then the {readmodel} read model contains` + table · `Then {message} is sent`.
- **When-vs-Then semantics mirror JasperFx's `ProjectionScenario`.** Arrange (`GivenEvents`) commits
  through a session; a failure there is critical and stops the scenario. The act (`WhenCommand`)
  **captures** the command's outcome — success or a domain/validation failure — into `LastError` so a
  `Then` can assert on it (that is what makes `Then validation fails with …` work). Assertions throw
  the new **`SpecAssertionException`** (in core: `ResultStatus.failed` at `FailureLevel.Assertion`, so
  they accumulate rather than abort).
- **Two refusal styles, two steps (issue #168, decided for option 2).** `Then validation fails
  with {string}` describes a refusal that *throws* — `executeCommandCore` populates `LastError`
  only from a caught exception. Wolverine's recommended messaging railway refuses *without*
  throwing (`HandlerContinuation.Stop` from a `Before`/`Load`), and `Then the command is refused`
  / `ThenCommandRefused()` describes that: dispatched, nothing thrown, nothing appended to the
  current stream. Deliberately **reason-less**: a clean stop's reason exists only as Wolverine's
  `Validation failure: …` log line (all three built-in continuation policies funnel there) and a
  bare `Stop` has no reason anywhere, so a reason clause would assert on nothing. A refusal that
  also notifies composes with `Then {message} is sent`. Messages are deliberately not constrained
  by the refusal step itself.
- **Store-agnostic, no Marten reference.** Everything reaches the store through `JasperFx.Events`
  resolved from the `IHostResource`. Two operations JasperFx.Events 2.37.0 has no abstraction for —
  **appending** arrange-events and **loading** a read-model document — go through the shared
  convention (`EventStoreAuthoring`: a session's `Events`/`SaveChangesAsync`/`LoadAsync<T>`), the same
  bounded softening as `EventStores`' aggregate/reset helpers. `RecordBuilding`/`EventTypeResolver`
  build command/event objects from table rows at runtime (the one runtime type-name lookup — the
  compile-time `{command}`/`{event}` captures never come there).
- **Ship as source.** The grammar `.cs` travels in the package under `contentFiles/cs/` (buildAction
  `None`, so a consumer never double-compiles it against the assembly) and `content/grammars/`, so
  VS Code's tree-sitter and Rider can parse the step source in a consumer's workspace. The *generator*
  needs no source — it reads the base fixture's steps from assembly metadata.
- **Proven** end to end by `Bobcat.CritterStack.Tests/GrammarSpecTests`: `Wallet.feature`, written
  only in shipped-grammar steps, compiles through the generator, runs on Marten (Postgres 5445) across
  four scenarios, and renders. **Fisher coverage is not possible yet** — every published Fisher needs
  JasperFx.Events ≥ 2.47.0, above the repo's 2.37.0 pin; that alignment bump is issue **#125**, in
  flight on another branch. The fixture binds to `JasperFx.Events`, so the same feature runs against a
  Fisher host by swapping `AddMarten` for `AddFisher` once the pin moves.

### Code-first specifications (`src/Bobcat/CodeFirst/`)

Issue #105, designed by use (decision of record 2026-08-21): a `Specification : Fixture` whose
`[Scenario]` methods *declare* `Given`/`When`/`Then` steps in C# — no `.feature`, no generator — and
land on the same `FeatureDefinition` / `DelegateExecutionStep` model, so they render, supervise and
report identically (`CodeFirstTwinTests` pins a Gherkin twin to the same `SpecRender` shape).
Registered with `runner.AddSpecification<T>()` / `ScanForSpecifications(assembly)`.

- **Compose, then execute.** The scenario method runs at plan-build time on a fresh instance; a
  value-producing `Given`/`When` hands back a `Captured<T>` read as `.Value` inside a later step.
  A step body returning a `Task` is awaited — hold a task you want to keep in a field.
- **A `Then` body that throws is an assertion failure, and the scenario continues** — the
  `ProjectionScenario` contract, and the one deliberate divergence from a Gherkin `[Then]` method
  (which the generator treats as critical). `Given`/`When` that throw are critical as ever.
- `Then(text, () => v).ShouldBe(...)`, `Then(() => v)` via `[CallerArgumentExpression]`,
  `ThenRows(...).KeyedBy(...).ShouldMatch(anonymous rows)` (set verification), `.WithRows(records)`
  on any step for a self-describing input table (`RowTable`), `Step(kind, text, raw)` escape hatch.
- `src/Bobcat.CodeFirst.Samples/` is the design-by-use project — Marten/Wolverine tests ported
  as specs against the root Postgres, collected by `dotnet test` (off-CI no-Postgres → zero tests,
  exit code 8 ignored). Its `CritterStackSpecification` is a stopgap for #104's
  `CritterStackFixture`. Verdict and open questions: `docs/code-first-specs.md`.

### Model (`src/Bobcat/Model/`) — Legacy
AST-based model from Phase 0-1 (Step tree, IGrammar, Sentence, etc). Being superseded by the source generator approach. Still used by some existing tests.

## Package Structure

| Package | Target | Status | Responsibility |
|---------|--------|--------|---------------|
| **Bobcat** | net10.0 | Active | Runtime: engine, rendering, resources, runner |
| **Bobcat.Generators** | netstandard2.0 | Active | Source generator: Gherkin parser, Cucumber Expressions, code gen |
| **Bobcat.Marten** | net10.0 | Active | MartenResource, step-context helpers, `[MartenEntities]` recipe |
| **Bobcat.EntityFrameworkCore** | net10.0 | Active | `[EfCoreEntities]` table-grammar persistence recipe |
| **Bobcat.Mtp** | net10.0 | Active | Runs Bobcat specs as a Microsoft.Testing.Platform test host |
| **Bobcat.Supervisor** | net10.0 | Active | Drives MTP hosts as worker processes; retry/isolation policy |
| **Bobcat.CritterStack** | net10.0 | Active | Wolverine tracked-session dispatch + event-store assertions over `JasperFx.Events` (Marten / Polecat / Fisher); see below |
| **Bobcat.Alba** | net10.0 | Planned | AlbaResource wrapping IAlbaHost |
| **Bobcat.Console** | net10.0 | Scaffold | Live test-progress web console (`dotnet bobcat`); see `docs/monitor-design.md` |

`Bobcat.Console` + `src/Bobcat.Console.FrontEnd/` (Vue 3 + Pinia + Element Plus + SignalR,
vitest-gated by `.github/workflows/console-frontend.yml`) deliberately mirror CritterWatch's
stack and palette. The viewer is a *consumer* of test runs over plain HTTP — no Bobcat.*
library may reference it. All decisions of record: `docs/monitor-design.md`. The publisher
side lives in core as `Bobcat.Monitoring` — dependency-free HTTP, opt-in via
`BobcatRunner.PublishToMonitor`, enabled by the real entry points only.

The viewer's **Event Model page** (issue #108) renders a JasperFx `EventModelDescriptor`
through `@jasperfx/event-model-vue` (`src/Bobcat.EventModel.FrontEnd/`, own gate
`event-model-frontend.yml`, consumed by CritterWatch too — the shared component is what makes
"renders identically in both viewers" true by construction). `PUT/GET /api/event-model` is a
**public wire contract** like `GET /api/runs`: one document, latest wins, persisted beside the
run archives, normalized through the typed descriptor on push (`EventModelStore`). Slices are
coloured from #107's run evidence by spec identity; the drill-down drawer shows each bound
spec's step results and flags touched types the model does not declare. Wiring gotchas: the SPA
consumes the package as a `file:` dependency whose gitignored `dist/` must be built first —
`console-frontend.yml` and the csproj `BuildFrontend` target both do, and the workflow's path
filter includes the package; and `Bobcat.Console` references `JasperFx.Events` **directly**
because at 2.54.0 the descriptor lives there and CPM pins only direct references (the
`DispositionKind` trap — transitively you get the pre-#687 sketch, which compiles and silently
drops `pattern`/`specifications`/`elements`).

The SPA's TypeScript mirrors of the event contracts are **generated, never hand-edited**:
`dotnet run --project src/Bobcat.Console -- generate` (NJsonSchema over
`Contracts/MonitorEvents.cs`, see `TypeScriptContracts`) rewrites `src/messages/monitor-events.ts`
and inserts any missing `relayToStore` case above its `*CASE ABOVE*` marker; hand-written cases
and the store handlers are left alone. `TypeScriptContractTests` fails the build when the
committed files drift. After adding a record to `MonitorEvents.cs` (both copies — the
`Bobcat.Monitoring` mirror stays deliberately separate, pinned by `ContractRoundTripTests`),
regenerate, then write the store handler the inserted case names.

**The project is `Bobcat.Console` — decided 2026-08-21, issue #100.** It was `Bobcat.Monitor`
from 2026-07-31 until then; "monitor" became ambiguous the moment agent coordination moved out
to Stoat, and `Bobcat.Viewer` lost to `Bobcat.Console` because "console" is what the tool is
called everywhere a user meets it (`dotnet bobcat`, "the live test-progress console"). The
rename covers the project, directory, assembly, NuGet package id, namespaces
(`Bobcat.Console.*`), the test/spec/frontend projects beside it, and the CI workflow
(`console-frontend.yml`). One C# consequence worth knowing: inside any `Bobcat.Console.*`
namespace — the viewer, its tests, its specs — an unqualified `Console.WriteLine` binds to the
*namespace* `Bobcat.Console`, so write `System.Console` there (`GenerateCommand.cs` does).

**What the rename deliberately did NOT touch, and must not be "finished" later:** the
publisher-side contract in core keeps the monitor vocabulary, because "monitor" there means
*the thing a run publishes to* and is user-facing or on the wire. That is the `Bobcat.Monitoring`
namespace (`src/Bobcat/Monitoring/`), `BobcatRunner.PublishToMonitor`,
`Supervisor.PublishToMonitor`, `MonitorPublisher` / `MonitorPublishingObserver`, the env vars
`BOBCAT_MONITOR`, `BOBCAT_MONITOR_URL`, `BOBCAT_MONITOR_DATA`, `BOBCAT_MONITOR_RETENTION_DAYS`,
`BOBCAT_RUN_ID`, `BOBCAT_RUN_TAG`, the `Monitor:*` configuration keys, the `/api/*` routes and
SignalR/ingest wire shapes, the CTRF reporter name, the duplicated `MonitorEvents.cs` records on
both sides, and `docs/monitor-design.md` (kept under its name because it documents that wire as
much as the viewer). Changing any of those is a breaking change that issue #100 scoped out;
it needs its own decision, not a sweep.

**`Bobcat.Console.Specs` is the viewer's end-to-end suite, written in Bobcat itself** (issue
#86). A spec project *may* reference `Bobcat.Console` — the no-reference rule is for libraries.
It is an MTP host (`BobcatTestApplication.Run`) with `IsTestProject=true` and the MTP properties
set by hand (the `*.Tests` convention in `Directory.Build.props` does not catch it), so `dotnet
test` at the root collects it. `MonitorHost` boots the real `Program` over Alba's TestServer with
a temp `Monitor:DataPath`; `ViewerSteps` is one shared `[IncludeGrammars]` module behind four shell
fixtures. `GET /api/runs/{id}` (per-scenario detail) was added for it and is a public wire
contract like `/api/runs`. A spec host publishes its own progress like any other; CI sets
`BOBCAT_MONITOR=0`. The framework gaps found writing it are on issue #62.

## Bobcat is MIT; AI agent coordination lives in Stoat

Split 2026-08-09. **Bobcat is the MIT integration testing framework** — Gherkin runner,
supervisor, and the test-run viewer. **[Stoat](https://github.com/JasperFx/stoat) is the BSL
AI agent coordination tool** (cross-repo plan DAGs, GitHub/NuGet observation, agent claims,
MCP). Everything under the viewer's `Coordination/` folder (then `src/Bobcat.Monitor/Coordination/`,
now `src/Bobcat.Console/`) moved there.

Two rules this creates, both load-bearing:

- **Nothing here may reference Stoat, and Stoat references nothing here.** Not one-way —
  *zero*. Stoat observes this repo's viewer over HTTP (`GET /api/runs`) on the same terms it
  observes GitHub and nuget.org. An MIT repo cannot depend on a BSL one, and keeping the
  dependency at zero makes that impossible to get wrong by accident.
- **No license gating in this repo.** Bobcat is free, entirely. If a feature seems to want a
  gate, it belongs in Stoat.

`GET /api/runs` is therefore a **public wire contract**, not an internal list model: it carries
`tag`, outcome counts, and scenario progress, and takes `?tag=`. Changing its shape breaks an
external consumer that has no assembly reference to warn it. `BOBCAT_RUN_TAG` is the
correlation hook — an opaque string Bobcat stamps on a run and never interprets (it was
`BOBCAT_PLAN_NODE`, renamed in the split because coordination vocabulary does not belong here).

## Key Dependencies

- **JasperFx** — Utility library (core Bobcat). Source at ~/code/jasperfx
- **Spectre.Console** — Terminal rendering
- **Microsoft.CodeAnalysis.CSharp** — Roslyn (generator only, compile time)

## Consuming Project Setup

```xml
<ProjectReference Include="Bobcat" />
<ProjectReference Include="Bobcat.Generators" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
<AdditionalFiles Include="Features/**/*.feature" />
```

## Design References

- `spec-driven-development-design.md` — Vision document: Gherkin, Critter Stack steps, failure semantics
- `.claude/plans/declarative-roaming-kazoo.md` — Implementation plan
- `docs/editor-integration.md` — Step completion / go-to-definition in VS Code (works, zero
  code, via the official Cucumber extension's tree-sitter query on `Given|When|Then` short names)
  and Rider (blocked on `Reqnroll.Rider`'s CLR-name gating; proposed upstream diff). Which
  attribute shapes each editor sees — `[Check]` and `[TableGrammar]` are invisible — and why a
  `[Then]` stacked on a `[Check]` is now guaranteed to stay a check.
- Alba source at ~/code/alba, JasperFx source at ~/code/jasperfx
