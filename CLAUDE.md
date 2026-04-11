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
`[Given("...")]`, `[When("...")]`, `[Then("...")]`, `[Check("...")]` using Cucumber Expression syntax (`{int}`, `{string}`, `{word}`, raw regex). `[Table]` for table data steps. `[SetVerification(KeyColumns = "...")]` for set comparison. `[SetUp]`/`[TearDown]` for lifecycle.

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
- **`SetVerificationComparer`** — Static comparison utility called by generated code
- **`SuiteResults`** — Cross-feature aggregation with exit codes (0=pass, 1=regression fail, 2=catastrophic)

### Rendering (`src/Bobcat/Rendering/`)
- **`SpecRender`** — Intermediate model (feeds both Spectre.Console and future HTML)
- **`CommandLineRenderer`** — Consumes `SpecRender`, outputs Spectre.Console ANSI markup
- **`SpectreProgressObserver`** — `IExecutionObserver` for live progress during long tests
- Set verification renders as table with per-cell coloring (green/red/yellow)

### Three-Level Failure Semantics
1. **Assertion** — `[Check]` returns false → continue (gather all failures)
2. **Critical** — Exception in any step → abort scenario
3. **Catastrophic** — `SpecCatastrophicException` → stop entire suite

### Model (`src/Bobcat/Model/`) — Legacy
AST-based model from Phase 0-1 (Step tree, IGrammar, Sentence, etc). Being superseded by the source generator approach. Still used by some existing tests.

## Package Structure

| Package | Target | Status | Responsibility |
|---------|--------|--------|---------------|
| **Bobcat** | net10.0 | Active | Runtime: engine, rendering, resources, runner |
| **Bobcat.Generators** | netstandard2.0 | Active | Source generator: Gherkin parser, Cucumber Expressions, code gen |
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
