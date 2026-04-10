# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is Bobcat?

Bobcat is a spec-driven integration testing framework for .NET, successor to Storyteller. It targets the Critter Stack ecosystem (Wolverine, Marten, Polecat) and aims to provide robust, human-readable integration testing with intelligent execution control.

## Build & Test Commands

```bash
# Build
dotnet build

# Run all tests
dotnet test

# Run a single test
dotnet test --filter "FullyQualifiedName~Bobcat.Tests.Model.CountsTests.tabulate_success"

# Run tests in a specific class
dotnet test --filter "FullyQualifiedName~Bobcat.Tests.Model.CountsTests"
```

All projects target .NET 10.0. Test framework is xUnit with Shouldly assertions and NSubstitute for mocking.

## Architecture

The codebase follows a **Plan → Execute → Results → Render** pipeline:

### Engine (`src/Bobcat/Engine/`)
Core execution pipeline:
- **`ExecutionPlan`** — Describes what to execute: list of `IExecutionStep` items, timeout, spec ID
- **`Executor`** — Runs steps sequentially with timeout/cancellation, applies `IContinuationRule[]` to decide whether to abort
- **`IExecutionStep`** — Protocol for a single executable test step
- **`IExecutionContext`** — Shared state during execution (lifecycle tracking, exception collection)
- **`IContinuationRule`** — Pluggable abort logic (fail-fast on critical errors)

### Model (`src/Bobcat/Model/`)
AST and grammar system (in-progress):
- **`Step`** — AST node with parent/children hierarchy
- **`IGrammar`** — Interface for domain-specific grammars that create execution plans and render results
- **`Fixture`** — Grammar for fixture types
- **`SpecificationRoot`** — Root node for a specification

### Result Tracking (`src/Bobcat/Model/`)
- **`StepResult`** — Outcome of a single step with timing, status, cell-level results, exceptions
- **`CellResult`** — Granular result for individual assertion cells
- **`Counts`** — Aggregation record tracking Rights, Wrongs, Errors
- **`ResultStatus`** — Enum: ok, success, failed, error, missing, invalid

### Rendering (`src/Bobcat/Rendering/`)
Spectre.Console-based output:
- **`CommandLineRenderer`** — Renders results as colored ANSI markup
- **`Line`/`Cell`/`Mode`** — Composable styled text segments

### Three-Level Failure Semantics
Per the design doc, failures have distinct severity:
1. **Assertion failure** — Wrong result, continue to next step
2. **Critical failure** — Infrastructure exception in Given/When, abort scenario
3. **Catastrophic failure** — System down, stop entire suite

## Key Dependencies

- **JasperFx** — Utility library (used in core Bobcat)
- **Spectre.Console** — Terminal rendering (used in ConsolePreview)

## Design Reference

`spec-driven-development-design.md` contains the comprehensive design document covering Gherkin integration, Critter Stack step definitions, and the full vision for the project.
