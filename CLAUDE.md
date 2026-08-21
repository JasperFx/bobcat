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
- **`SetVerificationComparer`** — Static comparison utility called by generated code
- **`SuiteResults`** — Cross-feature aggregation with exit codes (0=pass, 1=regression fail, 2=catastrophic)

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

**Not built yet: layer 2, the committed failure ledger.** The issue's own build order puts it
second, and its open questions (on-disk format, how a failure class is keyed, merge strategy for
concurrent CI appends, aging) are decisions rather than code. `RecoveryHint` is the shape it will
emit. The DECIDED fork stands: the ledger may *propose* a hint, but a human accepts it — a policy
that silently learns "just retry this" is exactly how red gets laundered into green with nobody
deciding to.

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
  surviving a crash on some hosts, so silence is absence of evidence. Indeterminate maps to exit
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
  tail, lane or null for a one-test process) whose default forwards to the string one, so
  either signature works for an observer. All of it reaches the monitor as wire events
  (`LaneStarted`/`LaneFinished`/`ResourceRecycled`/`WorkerFaulted`) via `SupervisorRunPublisher`;
  see `docs/monitor-design.md` Bobcat-side seams item 4 for what the dashboard folds from them.
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
  - Layer 2 (sleep-shaped-duration heuristics) and layer 3 (trend across runs) are **not built**:
    layer 2's false-positive question is open, and layer 3 needs the same committed ledger #44
    layer 2 does — one store, not two.

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
- **Two gaps in JasperFx.Events 2.37.0 are bridged by convention, and say so.** There is no
  abstraction for *aggregating* a stream or for *wiping* a store. `EventStores.AggregateStreamAsync`
  uses the read-only view when it is an `IQueryEventStore` (Marten, Polecat) and otherwise opens a
  session through the `IEventStore<TOps,TQuery>` closure and finds its `Events` member (Fisher).
  `EventStores.ResetAllDataAsync` follows `Advanced.ResetAllData(ct)` / `ResetAllDataAsync(ct)` /
  `Advanced.Clean.DeleteAll*` — the shape all three stores share — and a store matching none gets
  an exception naming what was looked for. JasperFx's own `IStatefulResource.ClearState` is *not*
  enough: Marten 9.22's database does not implement `IDatabaseWithRewindableState`, so
  `jasperfx resources clear` is a no-op for it (`ClearStatefulResourcesAsync` is still offered, for
  Wolverine's envelope storage). Both are the same bounded, documented softening as
  `GrammarBehaviors.Resolve`; when the abstraction lands upstream, the convention path is what gets
  deleted. `ProjectionScenario<,>` itself first ships in **JasperFx.Events 2.38.0** — above the pin —
  which is why the waits delegate to the database/daemon members it is built on rather than to it.
- **Fisher is the inner-loop target and is not yet covered in this repo, for a version reason, not a
  design one.** Every published Fisher (0.5.0+) requires JasperFx.Events ≥ 2.47.0; the aligned set
  that unlocks it is WolverineFx 6.29.1 ↔ Marten 9.28.0 ↔ JasperFx 2.53.0 ↔ Fisher 1.0.2 ↔ Polecat
  5.19.1 (`WolverineFx.Fisher` exists from 6.28.0). That is a repo-wide alignment bump
  (`docs/versions.md`, every sample), so it is a separate PR; the fallback paths above are what a
  Fisher host exercises, and they are unit-tested against a Fisher-shaped fake. Polecat 5.9.1 is the
  last release on 2.37.0 and needs SQL Server, so it is a documented manual run rather than a CI leg.

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
