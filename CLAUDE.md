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

# Run a specific test class
dotnet test --filter "FullyQualifiedName~Bobcat.Tests.EndToEnd.PipelineTests"

# Inspect generated source (look in obj/Debug/net10.0/generated/)
```

All projects target .NET 10.0 except Bobcat.Generators (netstandard2.0). Tests use xUnit + Shouldly + NSubstitute.

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

### Runtime (`src/Bobcat/Runtime/`)
- **`BobcatRunner`** — CLI entry point. Discovers features, manages suite lifecycle, renders results.
- **`FeatureDefinition`** / **`ScenarioDefinition`** — compiled feature structure from generator
- **`TestSuite`** — Named resource registry (start/reset/teardown lifecycle)
- **`ITestResource`** — Database, IHost, Docker container, etc.
- **`IHostResource`** — A resource that owns a DI container. Exposes `RootServices` (the host's
  root container) and `CurrentServices` (the per-scenario scope), and owns the scope itself via
  `BeginScenarioScope()`/`EndScenarioScope()`. `CurrentServices` **throws** outside a scenario —
  there is no silent root fallback.
- **`SetVerificationComparer`** — Static comparison utility called by generated code
- **`SuiteResults`** — Cross-feature aggregation with exit codes (0=pass, 1=regression fail, 2=catastrophic)

### Per-Scenario DI Scope
Each scenario runs as `ResetAll()` → `BeginScenarioAll()` → scenario → `EndScenarioAll()`. Persistent
state (DB rows, queues) is cleaned first, then a fresh DI scope is opened over it. Scope disposal
resets service *instances*; `ResetBetweenScenarios` resets *persistent* state — both matter.

Step/grammar parameter binding: `IStepContext` and any type a Gherkin cell can't produce are
resolved from the scenario scope; a name matching a data-table header wins over convention
injection. Overrides: `[FromScopedService]`, `[FromRootService]`, `[FromKeyedServices]` (all take
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

**`dotnet test` caveat (verified 2026-07-28, SDK 10.0.101):** running an MTP host through
`dotnet test` requires opting the *whole repo* into MTP mode with a root `global.json`
(`{"test":{"runner":"Microsoft.Testing.Platform"}}`). This repo cannot do that — the xUnit 2.9.3
projects are VSTest-based and would stop running. The host executable itself works directly
(`./MySpecs`, `--list-tests`, `--filter-uid <uid>`), which is what `Bobcat.Mtp.Tests` exercises.
A control run of xUnit v3 — a shipping MTP host — behaves identically under `dotnet test` here,
so this is a toolchain constraint rather than something Bobcat.Mtp does wrong.

`Bobcat.Mtp.SampleHost` is a spec project run as a host; `IsTestProject=false` keeps `dotnet
test` from collecting its deliberately-failing scenarios, and `Bobcat.Mtp.Tests` launches it as
an executable instead.

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
| **Bobcat.CritterStack** | net10.0 | Planned | Wolverine/Marten/Polecat steps, TrackedSession |
| **Bobcat.Alba** | net10.0 | Planned | AlbaResource wrapping IAlbaHost |

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
- Alba source at ~/code/alba, JasperFx source at ~/code/jasperfx
