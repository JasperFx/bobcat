# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is Bobcat?

Bobcat is a spec-driven integration testing framework for .NET, successor to Storyteller.
Specifications are ordinary xUnit v3 / TUnit / NUnit / MSTest tests — the **projected** model —
which a Roslyn source generator and the runtime recorder render as readable specifications (step
interceptors on `[Given]`/`[When]`/`[Then]`/`[Step]`/`[Check]` helpers, marker comments, declared
values, the Event Model fold).

> **The Gherkin lane was removed in 0.50.0 (bobcat#471)**, with diagnostics BOBCAT001–022, 030, 031
> and 035 (ids retired, never reused). Everything below is the projected model.

## Feature work does not edit CLAUDE.md

**Don't add to CLAUDE.md or docs/agents/ as part of feature work.** A feature's decisions of record
go in its issue and PR description, its user-facing docs page under docs/, and (for a cross-cutting
design decision) the matching design/ record's Discussion log. Edit these agent files only when an
always-on rule for agents changes — a build or test command, a repo convention, a version pin, or a
trap that would mislead the next agent working anywhere in the repo — and keep that edit to the
smallest change that states the rule.

## Every new capability ships with its ai-skills entry

**Any new Bobcat capability — a step, attribute, diagnostic, runner option, env var, tool command
or runtime behavior — also gets written up in [ai-skills](https://github.com/JasperFx/ai-skills)
(local checkout `~/code/ai-skills`).** The point is teaching a *later agent* to troubleshoot
Bobcat on its own: what an error or surprising result means, and the move that fixes it. An agent
consuming Bobcat in another repo never sees this file or the source; the skill is all it has.

- **Where:** `skills/critterstack-sdd-bobcat-authoring/SKILL.md` today. A new diagnostic goes in
  its diagnostics table (`| BOBCATnnn | what triggers it | the fix |`). When the troubleshooting
  material outgrows that skill, split it into a `critterstack-sdd-bobcat-troubleshooting` skill
  rather than letting the authoring skill sprawl.
- **What to write:** the symptom as the user sees it (the diagnostic id and message, the
  run-time cell status such as `invalid` vs `failed`, the exit code, the console output), the
  cause, and the fix — not a tour of the implementation. Include the "it is *not* this" cases that
  cost us time, e.g. a check that is skipped and left to run time.
- **When:** in the same piece of work as the capability, as a PR to ai-skills linked from the
  Bobcat PR — not as a later clean-up. Follow ai-skills' own `CLAUDE.md` (quote YAML descriptions
  containing colons, then `npm run docs:generate`) and update the skill's `description` when the
  new capability is something an agent would search for.

## Build & Test Commands

```bash
# The Nuke build (build/) — tests.yml runs `CI`, publish.yml `CI Pack`. Default target is Test.
./build.sh                          # restore → compile → test
./build.sh Docker CI                # start compose services, then exactly what tests.yml runs
./build.sh Pack                     # packages to artifacts/packages (never touches artifacts/local-feed)
./build.sh UiSmoke                  # what ui-smoke.yml runs: pack the tool with its UI, smoke `bobcat ui` (needs Node; BOBCAT_UI_SMOKE_SKIP_BROWSER=1 skips Chromium)
./build.sh --help                   # every target and parameter

# Build everything (bobcat.slnx is the only solution at the root, so it is picked up implicitly)
dotnet build

# Run unit tests
dotnet test

# Start the compose services (Postgres on 5445, so it never collides with a Postgres you already
# run). The Marten-backed tests left with the Critter Stack vocabulary for the Wolverine repo in
# #744; the Docker target and compose file stay.
docker compose up -d

# Run a specific test class. Tests run on Microsoft.Testing.Platform, not VSTest, so the
# old `--filter "FullyQualifiedName~X"` is gone. global.json puts `dotnet test` in MTP mode, so
# test-host options go straight on the command line (no `--`) and the project is `--project`.
dotnet test --project src/Bobcat.Tests/ --filter-class "*SpecAssert*"
dotnet test --project src/Bobcat.Tests/ --filter-method "*passes_on_retry*"

# A test project is a self-executing MTP application, so it can also be run directly:
./src/Bobcat.Tests/bin/Debug/net10.0/Bobcat.Tests --list-tests

# Inspect generated source (look in obj/Debug/net10.0/generated/)
```

Most shipped packages multi-target **net9.0 and net10.0**; `Bobcat.Console` and every test host
targets net10.0 only, and
`Bobcat.Generators` targets netstandard2.0 (a Roslyn analyzer). See Package Structure in `docs/agents/build-and-test.md`.

Tests use **xUnit v3 + Shouldly + NSubstitute**, running on **Microsoft.Testing.Platform** rather
than VSTest.

Every `*.Tests` project is therefore a self-executing MTP test host — `OutputType=Exe`,
`UseMicrosoftTestingPlatformRunner`, `TestingPlatformDotnetTestSupport` are set once in
`src/Directory.Build.props` for any project whose name ends in `.Tests`, so a new test project
cannot silently fall back to VSTest. There is no `Microsoft.NET.Test.Sdk`, no
`xunit.runner.visualstudio`, and no `coverlet.collector` (a VSTest data collector) — MTP supplies
its own equivalents.


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

## Area files

The rest of this guide lives in per-area files under `docs/agents/`, imported below so every session
loads them. When one of those rules changes (see "Feature work does not edit CLAUDE.md" above),
edit the area's file rather than this index.

- @docs/agents/build-and-test.md — solutions, Central Package Management, the Nuke build and CI targets, the MTP version pin, database-backed tests, the package table, consuming-project setup. Read before touching a csproj, a props file, `build/` or a workflow.
- @docs/agents/conventions.md — what the naming rule does not rename, how the codebase was renamed, and the generator's `global::` rule. Read before a rename sweep or when emitting generated code.
- @docs/agents/projected-model.md — the projected pipeline, step attributes, tables and decision tables, partial objects, structural comparison, BOBCAT032. Read when working in `src/Bobcat/Runtime/`, `src/Bobcat/Partial/` or the generator's step and table code.
- @docs/agents/event-model.md — Event Modeling slices, the generated `EventModelSliceDescriptor`, roles, triggers and routes, query slices, pending specs. Read when working on `EventModelEmitter`, `ProjectedRoles` or anything that feeds the Event Model.
- @docs/agents/runners-and-identity.md — the xUnit/TUnit/NUnit/MSTest adapters, `[BobcatSpec]`, the per-request run bracket, spec identity, the spec manifest and running by identity. Read when touching an adapter or `SpecIdentity`/`SpecManifest`/`SpecFilterArguments`.
- @docs/agents/engine-and-rendering.md — cell comparisons, scenario reports, test output, progress, run evidence, timing, rendering and the three failure levels. Read when working in `src/Bobcat/Engine/` or `src/Bobcat/Rendering/`.
- @docs/agents/resilience-and-supervisor.md — dispositions, retry budgets, recovery hints, the committed ledger, the supervisor, parallel lanes, stalls, memory sampling, environment checks. Read when working in `src/Bobcat/Resilience/`, `src/Bobcat/Ledger/` or `src/Bobcat.Supervisor/`.
- @docs/agents/critter-stack.md — where the Critter Stack vocabulary went (WolverineFx.Bobcat, #744) and what stayed in core (`RecordBuilding`). Read before adding anything that would need JasperFx or Wolverine.
- @docs/agents/monitor-wire.md — the monitor vocabulary and environment variables, `BOBCAT_MONITOR_URL` fan-out, the spec half of the Event Model, supervised-run progress, `StepCell`. Read before changing anything in `src/Bobcat/Monitoring/`: it is a public wire contract.
- @docs/agents/console-and-tool.md — `bobcat ui`, the `bobcat` tool and `import-event-model`, the resident runner, and the MIT/Stoat boundary. Read when working in `src/Bobcat.Console/`, `src/Bobcat.Ui*`, `src/Bobcat.EventModel/` or `src/Bobcat/Residency/`.

## Key Dependencies

- **No JasperFx and no Wolverine in core (#744)** — `Bobcat` references neither; `BobcatAssemblyReferenceTests`
  guards it. JasperFx(.Events) is referenced only by the tools (`Bobcat.EventModel`, `Bobcat.Console`,
  `Bobcat.Ui`, which also keeps WolverineFx.Http/SignalR) and by test hosts that compile the Event Model.
  The Critter Stack vocabulary and the samples live in the Wolverine repo (WolverineFx.Bobcat). Design:
  `design/jasperfx-decoupling.md`. JasperFx source at ~/code/jasperfx
- **Spectre.Console** — Terminal rendering
- **Microsoft.CodeAnalysis.CSharp** — Roslyn (generator only, compile time)

## Design References

- `docs/` — the user-facing docs: `getting-started.md` (package setup), `marker-steps.md` (writing
  specifications), `xunit.md` / `tunit.md` / `nunit.md` / `mstest.md`, `spec-identities.md` (#391
  and #338's `SpecIdentityAudit`), `resident-runner.md`, `warm-projected-runs.md` (#394's findings),
  `parallel-ready-suites.md`, `bobcat-tool.md`, `monitor-design.md` (the wire contract and the
  receiving-side folds), and `compatibility.md` (version policy and the rc.2 migration notes)
- `docs/tutorials/` — `specifications-with-code.md`, `data-intensive-specifications.md` (tables,
  set verification, decision tables, cell expressions and the four row markers),
  `agent-friendly-tests.md`, `event-modeling.md`, `continuous-integration.md`,
  `ide-integration.md`, `reliable-integration-testing.md`
- `design/` — internal records: `ledger-design.md` (the committed ledger),
  `critter-stack-interop-notes.md`, `versions.md`, `wolverine-ci-rollout.md`, `jasperfx-decoupling.md` (#744/#745); `design/history/`
  keeps the Gherkin-era records (`spec-driven-development-design.md`, `acceptance-coverage-map.md`,
  `sample-wiring-playbook.md`)
- JasperFx source at ~/code/jasperfx
