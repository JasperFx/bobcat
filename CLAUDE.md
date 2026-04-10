# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is Bobcat?

Bobcat is a spec-driven integration testing framework for .NET, successor to Storyteller. It targets the Critter Stack ecosystem (Wolverine, Marten, Polecat) and aims to provide robust, human-readable integration testing with intelligent execution control. Tests are C#-first with optional Gherkin support planned.

## Build & Test Commands

```bash
# Build
dotnet build

# Run all tests
dotnet test

# Run a single test
dotnet test --filter "FullyQualifiedName~Bobcat.Tests.EndToEnd.PipelineTests.full_pipeline_passing_scenario"

# Run tests in a specific class
dotnet test --filter "FullyQualifiedName~Bobcat.Tests.Model.CountsTests"

# Run the ConsolePreview demo
dotnet run --project src/ConsolePreview/
```

All projects target .NET 10.0. Test framework is xUnit with Shouldly assertions and NSubstitute for mocking.

## Architecture

The codebase follows a **Model (AST) → Plan → Execute → Results → Render** pipeline.

### Step Attributes (`src/Bobcat/Attributes.cs`)
Fixture methods are marked with `[Given("...")]`, `[When("...")]`, `[Then("...")]`, or `[Check("...")]` attributes using Gherkin Expression syntax. These mirror Reqnroll's attribute shape (without the dependency) for future compatibility. `[SetUp]` and `[TearDown]` mark fixture lifecycle methods.

**Important:** `[Check]` (not `[Fact]`) is used for boolean assertions to avoid collision with xUnit's `[Fact]`.

### Fixtures (`src/Bobcat/Fixture.cs`, `src/Bobcat/Model/FixtureModel.cs`)
- **`Fixture`** — Base class for test fixtures. Subclass and add attributed methods.
- **`FixtureModel`** — Discovered structure of a fixture class (grammars, setup, teardown). Built once via reflection.
- **`FixtureInstance`** — Live instance for a scenario execution.

### Grammar System (`src/Bobcat/Model/`)
- **`IGrammar`** — Interface: `Name`, `CreatePlan()`. Each grammar knows how to add `IExecutionStep`s to an `ExecutionPlan`.
- **`Sentence`** — Wraps an attributed method. Resolves parameters from Step values.
- **`Fact`** — Wraps a `[Check]` method returning `bool`/`Task<bool>`. Returns Assertion failure (not Critical) on false.
- **`Step`** — AST node tree. `Step.Add(grammar)` generates name-based IDs with dedup suffixes.
- **`SpecificationRoot`** — Root grammar that walks children to build the plan.

### Engine (`src/Bobcat/Engine/`)
- **`IStepContext`** — Narrow interface visible to step execution code: `GetService<T>()`, `Log()`, `AttachDiagnostic()`.
- **`IExecutionContext`** — Engine-internal interface extending `IStepContext` with lifecycle methods.
- **`SpecExecutionContext`** — Concrete implementation (named to avoid `System.Threading.ExecutionContext` collision).
- **`Executor`** — Runs steps sequentially with timeout, cancellation, and `IContinuationRule[]`.
- **`StepKind`** — Given, When, Then, SetUp, TearDown. Drives automatic failure classification.
- **`FailureLevel`** — None, Assertion, Critical, Catastrophic. Set by `StepResult.MarkErrored()` based on exception type and `StepKind`.
- **`FailureLevelContinuationRule`** — Stops on Critical/Catastrophic, continues on Assertion.
- **`SpecCriticalException`** / **`SpecCatastrophicException`** — Throw to force failure level.

### Three-Level Failure Semantics
1. **Assertion** — `[Check]` returns false → continue to next step (gather all failures)
2. **Critical** — Exception in any step, or `SpecCriticalException` → abort scenario
3. **Catastrophic** — `SpecCatastrophicException` → stop entire suite

### Rendering (`src/Bobcat/Rendering/`)
- **`CommandLineRenderer`** — Renders `ExecutionResults` as Spectre.Console ANSI markup with status icons, step kinds, timing, exception messages.
- **`Line`/`Cell`/`Mode`** — Composable styled text segments for inline rendering.

## Key Dependencies

- **JasperFx** — Utility library (used in core Bobcat)
- **Spectre.Console** — Terminal rendering

## Design References

- `spec-driven-development-design.md` — Comprehensive design document covering Gherkin integration, Critter Stack step definitions, failure semantics, and the full vision.
- `.claude/plans/declarative-roaming-kazoo.md` — Phased implementation plan (Phase 0 through Phase 5).

## Package Structure (Planned)

| Package | Status | Responsibility |
|---------|--------|---------------|
| **Bobcat** | Active | Core: engine, model, grammars, fixtures, rendering |
| **Bobcat.Testing** | Planned | xUnit adapter via source generators |
| **Bobcat.Gherkin** | Planned | .feature file parsing and step definition discovery |
| **Bobcat.CritterStack** | Planned | Wolverine/Marten/Polecat step definitions |
| **Bobcat.Reqnroll** | Planned | Reqnroll compatibility bridge |
