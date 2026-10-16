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
# The Nuke build (build/) — tests.yml runs `CI`, publish.yml `CI Pack`, samples.yml `Samples`. Default target is Test.
./build.sh                          # restore → compile → test
./build.sh Docker CI                # start compose services, then exactly what tests.yml runs
./build.sh Pack                     # packages to artifacts/packages (never touches artifacts/local-feed)
./build.sh Docker Samples           # what samples.yml runs: build every sample, run their specs (Postgres + Fisher)
./build.sh UiSmoke                  # what ui-smoke.yml runs: pack the tool with its UI, smoke `bobcat ui` (needs Node; BOBCAT_UI_SMOKE_SKIP_BROWSER=1 skips Chromium)
./build.sh --help                   # every target and parameter

# Build everything (bobcat.slnx is the only solution at the root, so it is picked up implicitly)
dotnet build

# Run unit tests
dotnet test

# Start the local Postgres the Marten integration tests use (published on 5445 so it
# never collides with a Postgres you already run). Without it those tests SKIP locally
# — on CI they never skip, so a missing database fails the build.
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

Most shipped packages multi-target **net9.0 and net10.0**; `Bobcat.Console` and every test and
sample host target net10.0 only, and
`Bobcat.Generators` targets netstandard2.0 (a Roslyn analyzer). See Package Structure below.

Tests use **xUnit v3 + Shouldly + NSubstitute**, running on **Microsoft.Testing.Platform** rather
than VSTest.

Every `*.Tests` project is therefore a self-executing MTP test host — `OutputType=Exe`,
`UseMicrosoftTestingPlatformRunner`, `TestingPlatformDotnetTestSupport` are set once in
`src/Directory.Build.props` for any project whose name ends in `.Tests`, so a new test project
cannot silently fall back to VSTest. There is no `Microsoft.NET.Test.Sdk`, no
`xunit.runner.visualstudio`, and no `coverlet.collector` (a VSTest data collector) — MTP supplies
its own equivalents.

### Solutions, packages and the build

- **Solutions are `.slnx`, everywhere** — `bobcat.slnx`, one per sample
  (`samples/*/*.slnx`), and `spikes/mtp-orchestration/MtpSpike.slnx`. There are no `.sln` files
  left; converted with `dotnet sln migrate` on 2026-09-28. The samples and the spike are
  deliberately **not** in `bobcat.slnx`.
- **Central Package Management, one file per tree**, never an inline `Version=` on a
  `PackageReference`: `src/Directory.Packages.props`, `samples/Directory.Packages.props` (its
  Critter Stack versions must stay **identical** to src's — read src's header before moving
  JasperFx(.Events) on its own), and `spikes/mtp-orchestration/Directory.Packages.props` (kept
  apart because the spike measured MTP 2.x, which src cannot take). **`build/` opts out** in its own
  `Directory.Packages.props` and pins inline, like the JasperFx repo's build: the Nuke bootstrap
  stays self-contained, and the root file must not grow Nuke.Common.
- **The Nuke build (`build/`) is the one definition of CI** — `Build.cs` (Restore / Compile / Test /
  `CI` / Pack / Docker) and `Samples.cs` (CompileSamples / TestSamples /
  TestSamplesOnFisher / `Samples`) and `UiSmoke.cs` (`UiSmoke`). `tests.yml` runs `./build.sh CI`,
  `publish.yml` runs `./build.sh CI Pack`, `samples.yml` runs `./build.sh Samples`, `ui-smoke.yml`
  runs `./build.sh UiSmoke`. **Change what CI does in `build/`,
  not in a workflow** — the workflows only supply services (Postgres) and environment.
  Configuration defaults to Debug locally and Release on a CI server.
  - `Docker` (`docker compose up -d --wait`) is never a dependency of `Test`: someone pointing
    `BOBCAT_POSTGRES` at their own database must not have Docker started for them.
  - **`Pack` writes to `artifacts/packages` and `Clean` touches only that.** `artifacts/local-feed`
    is a hand-maintained NuGet feed consumed by `samples/BankAccountES/NuGet.config` and the
    Wolverine CI branch — never clean `artifacts/` wholesale. `publish.yml` pushes
    `artifacts/packages/*.nupkg`.
  - **Every package carries the root `LICENSE`** (`PackageLicenseFile`, packed from
    `src/Directory.Build.props` by path — not copied, unlike `src/icon.png`). It replaced
    `PackageLicenseExpression` (NuGet refuses both, NU5033), so nuget.org shows a license *file*.
  - `CompileSamples` builds every `samples/**/*.csproj` (not the `.slnx` files, so a project in no
    solution is still caught) against `QuarantinedSamples` — currently empty. A quarantined project
    that *builds* fails too, so the list can only shrink. `TestSamples` creates one Postgres
    database per sample with Npgsql (not the `createdb` CLI, so it works on a laptop; an existing
    database is reused) and passes it as `ConnectionStrings__Marten`; `SamplesPostgres` overrides
    the server. `TestSamplesOnFisher` runs BankAccountES on a SQLite file in `.nuke/temp`.

**Version pin that matters:** `Microsoft.Testing.Platform` is held at **2.5.1** because that is
what `xunit.v3` 4.0.2 builds against (its runner package is `xunit.v3.core.mtp-v2`), and
`TUnit.Engine` 1.74.0 wants the same. It was 1.9.1 under `xunit.v3` 3.2.2 (`xunit.v3.core.mtp-v1`)
until bobcat#470; the platform and xUnit move together. MTP 2.x refuses the VSTest bridge on the
.NET 10 SDK, so the root `global.json` opts `dotnet test` into its MTP mode: pass `--solution` /
`--project` (never a positional path), give test-host options directly (no `--` separator), and
note that mode collects every project with `IsTestingPlatformApplication=true` — `IsTestProject=false`
no longer opts a host out (see `Bobcat.Xunit.Samples.csproj`).

**Database-backed tests:** `Bobcat.CritterStackScenario.Tests` exercises the store plumbing against a
real Postgres via `[PostgresFact]`. Connection string comes from `BOBCAT_POSTGRES`, defaulting to
the `docker-compose.yml` instance on **5445**. The skip is deliberately disabled when `CI=true`,
so CI can never report a silent pass for a missing database.

**Generator note:** every type name the generator emits *as a type* (`typeof(...)` in the Event
Model source, interceptor signatures) must be `global::`-qualified: generated code lives in its own
namespace, where an unqualified name can bind to the wrong type.

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
from .NET's `[GeneratedRegex]` (see `MarkerSpecNaming.cs`) — Bobcat's own generator emits none, so
there is nothing to fix in its templates.

The codebase was converted in one pass using a Roslyn `Renamer`, not by hand or by regex — a
textual rename of a name like `Record` would have hit unrelated identifiers. The same tool is the
right way to sweep up after a long-lived branch merges.

One gotcha when doing that: a member whose camel form is a C# keyword (`Class` → `class`) is
escaped to `@class` rather than rejected, so it compiles and the sweep looks clean. Grep the diff
for `@` identifiers afterwards and give those a real name instead — `testsInClass`, not `@class`.

## Architecture

### The projected pipeline

An ordinary test calls step helpers → **Bobcat.Generators** (compile time) intercepts those calls
and reads the test's marker comments → the runner adapter opens a recording per test →
**`ScenarioRecorder`** (run time) records steps, cells and verdicts → **`SpecRender`** → console,
JSON report, the platform's own test output, and the monitor wire.

- **Step interceptors** (`StepInterceptors`): a call to a method carrying a step attribute becomes a
  recorded step, and a `SpecAssert` inside it records onto that step. Projected Shouldly assertions
  (`ProjectedAssertions`) are the opt-in second source.
- **Marker comments and declared values** (`MarkerCommentSpecs`, `DeclaredValues`): `// Given …`
  comments in a test body are steps too; `DeclaredSteps` holds what each test declares, keyed by
  spec identity.
- **Specification bindings** (`SpecificationManifest`, `MarkerSpecNaming`): one identity per
  `[BobcatSpec]`/`[BobcatScenario]` test, the Event Model fold (`EventModelEmitter`) and the spec
  manifest all read the same names.
- **Runner adapters** (`Bobcat.Xunit`, `Bobcat.TUnit`, `Bobcat.NUnit`, `Bobcat.MSTest`) open and
  close the recording; core names none of their types.

User-facing docs: `docs/marker-steps.md`, `docs/tutorials/specifications-with-code.md`.

### Step attributes (`src/Bobcat/Attributes.cs`)

`[Given]`, `[When]`, `[Then]`, `[Check]` and the keywordless `[Step]` are **one family** —
`StepAttribute` is the base, and a project's own attribute deriving from `GivenAttribute` inherits
the keyword (read off the attribute's type, never by instantiating it). Two expression syntaxes,
decided **per placeholder**: Cucumber captures by type (`{int}`, `{string}`, `{word}`, the Event
Modeling type words) and Storyteller-style named templates by parameter name (`{sum}`); a built-in
type word wins, and mixing is allowed. `[BobcatStep]` (`src/Bobcat/MarkerSteps/`) is the same family
for a shared helper.

- **`[Check]` (not `[Fact]`) for boolean assertions** — avoids the xUnit collision.
- **`[Header("…")]`** on a row-method parameter or a compared property names its column. The title
  **replaces** the name; it is not an alias.
- **`Bobcat.StepTable`** — a step parameter receiving a table literal whole (headers + rows), for
  `TableRunner`, set verifications and partial-object tables. Never resolved from DI.

### Event Modeling slices and descriptors (issues #104/#106)

A projected test declares the non-derivable bits of an Event Modeling slice on `[BobcatSlice]` or
`[BobcatSpec]`: the slice (`SliceType`, or a literal `SliceName`), `Domain`, `Chapter`, `Pattern`
and `TriggeredBy`. `GeneratorSliceTags` reads the first four in the generator; `TriggeredBy` is read
on its own (#463, below). A method-level binding **replaces** the class's for that test rather than
overriding part of it — a rebinding is a whole binding, so a test that wants the class's domain too
says so. A method's `TriggeredBy` beats its class's whatever order tests fold in
(`TriggerLabelDeclaredOnScenario`, the #258 rule).

**The generator turns these into a JasperFx `EventModelSliceDescriptor` (issue #106)** —
Bobcat is the first real implementation of `IEventModelDefinitionSource` anywhere. One
`BobcatEventModelSource.g.cs` per assembly, registered with `services.AddBobcatEventModel()`.

- **`[assembly: EventModelName("…")]` names the model; the assembly name is only the fallback
  (issue #172).** Upstream, `EventModelDiscovery.Assemble` folds descriptors together **by model
  name**, and the Wolverine-derived model is named for the *service* (`opts.ServiceName`) — so a
  spec assembly called `BankAccountES.Tests` can never merge with the chains it describes unless
  it declares the service's name. The attribute renames only the descriptor; the source's
  `Subject` URI keeps the assembly name, because two spec assemblies may legitimately feed one
  model. `samples/BankAccountES` is the worked example (`Tests/AssemblyAttributes.cs`), and its
  `EventModel.cs` specs pin the merge end to end.
- **The generated source is `internal`, and that has a consequence:** the app host cannot see the
  spec assembly's slices (the reference points the other way), so composing all four design-time
  sources — chains + store + overlay + specs — inside one process only works in the spec runner's,
  where `samples/BankAccountES/Tests/EventModelSteps.cs` does it. **The store rung** (issue
  #300, jasperfx#825): since JasperFx.Events 2.69 every Critter Stack store registers a
  `ProjectionEventModelSource` from `AddMarten`/`AddFisher`/`AddPolecat` — one `View` slice per
  registered projection, named after the **document type**, carrying `ProjectionTypes`,
  `ReadModelTypes` and `ConsumedEvents` from the projection's real apply set. Slices merge by name,
  so a spec-declared View slice folds into it only if the binding names the document type exactly
  (`[BobcatSpec(typeof(Account))]`); `AccountView.cs` + `EventModel.cs` in the sample pin "one
  slice, not two", and the falsification (mis-bind it) shows as the `Specifications`/`Domain` roles
  going unclaimed on the store's slice, not as a second slice appearing. A handler that starts a
  stream through `Storage.StartStream` needs Wolverine's `[Emits(typeof(…))]` before any source
  knows what it appends — the event is built in the body, so nothing on the signature says it
  (wolverine GH-4386) — and without it the store's view has no `EventConsumed` link back to that
  slice. The host's own `event-model --url` export (and therefore Stoat's `watch-event-model`)
  pushes a document *without* the spec-declared slices or their `Specifications` bindings. **The two
  halves meet on the console — Stoat's — instead:** #268 made `/api/event-model` merge per source
  and #294 made the runner publish the spec half as it attaches, so each producer pushes what it can
  see and `GET` folds them by slice name. That is the whole reason the `[EventModelName]` above is
  load-bearing rather than cosmetic — the merge is keyed on the model name at both altitudes.
- **An overlay's human trigger label on an HTTP slice works again as of WolverineFx 6.31.0**
  (wolverine#4181/#4182, both fixed by wolverine#4185). Before it, the HTTP-derived source
  claimed `TriggerLabel` with the verb+route, so the overlay's label lost the merge and minted a
  noise `SourceDisagreement` hotspot per labelled slice — and a query endpoint returning
  `IReadOnlyList<T>` reported the raw generic CLR string as its read model. The specs in
  `samples/BankAccountES/Tests/EventModel.cs` that assert the fixed behaviour are the regression
  guard — don't delete them as redundant.
- **A slice accumulates across the whole compilation.** Several specs usually describe the same
  slice, and a slice may span several test classes; every test bound to it feeds one descriptor.
- **Only roles are stamped, never a graph.** `EventModelSliceDescriptor.Elements`/`.Edges` are
  *computed upstream* from the typed roles on every read. Building the element graph in the
  generator would be a second opinion about the same slice, which is what "computed on read"
  exists to prevent.
- **`Chapter` is the third slice setting (issue #298, canvas design decision 5).** A chapter is the
  span of the timeline a slice belongs to — the grouping every Event Modeling tool zooms into
  (eventmodelers.ai's wide arrow, Miro's frames, emlang's per-chapter documents) — and it is
  **independent of `Domain`**: a bounded context has many chapters. `EventModelEmitter` stamps it
  onto JasperFx 2.69's `Chapter` role (which merges like `Domain`: per claim, first wins on a tie, a
  genuine disagreement is a `SourceDisagreement` hotspot). `EmlangImport` keeps the board's chapter
  name — every imported slice carries the chapter it was segmented from, and a slice folded from a
  second chapter **keeps the first** with a report line saying so. `@jasperfx/event-model-vue` 0.12.0
  draws **one band per contiguous run** of same-chapter slices; **the canvas never reorders
  slices**, and whether something upstream should order chapters is an open decision (2026-09-15).
- **An arranged event is what a View slice consumes, and nothing on a Command slice (issue #297,
  canvas design decision 3).** Arranging `AccountOpened` and then asserting the Account read model
  is the best evidence there is that the projection applies `AccountOpened`, and *consumed* is a
  different claim from *emitted* — JasperFx 2.69's `ConsumedEvents` role, the `EventConsumed` link,
  the State View arrow. On a Command slice the same arrangement is the aggregate's own stream, and
  stamping it would draw an arrow from `OpenWallet` into every slice that merely starts from an
  open wallet. Mechanics: the `ArrangedEvent` role collects into `SliceModel.ArrangedEvents`, and
  `emitSlice` writes `ConsumedEvents` only when `patternOf` is `View` — so the decision is per
  *slice*, after every test has folded in (one acting test makes the whole slice Command). **Type
  lists merge by equality, not union**: a View slice whose specs between them arrange fewer events
  than the store's projection applies is a `SourceDisagreement` hotspot, and the right reading (the
  view is under-specified). The sample's two `AccountView.cs` specs arrange all four of the Account
  projection's events so the store rung and the spec rung agree; drop one and the sample goes red.
  `ReadsFrom` reaches a model through the importer, and from a spec through `SliceRole.Context`
  (`GivenContext`, #502, below).
- **The command is the act — the last `Command` call, not the first one named (#464).** Specs
  routinely arrange by issuing earlier commands (an `OpenWallet` before the `CreditWallet` the test
  is about), so first-wins mislabelled the slice. Arrange commands are not lost: they stay in the
  specification's `ResolvedTypes`, which is where "this spec touched that type" belongs and what
  #107's run evidence joins on.
- **Gated on the reference, by type probe.** Nothing is emitted unless the consuming compilation
  has `JasperFx.Events.EventModeling.EventModelSliceDescriptor` — most Bobcat suites do no event
  sourcing. A type probe rather than an assembly-name check because JasperFx.Events **2.53.0
  shipped an early, incompatible sketch** of that namespace; "the assembly is referenced" and
  "the shape emitted against exists" are different questions. Requires **2.54.0+**.
- **Spec identity is `{Feature}/{Scenario}`** — the same string `scenario_finished` carries, so
  design-time and run evidence join with no mapping table.
- **`Pattern` is derived only where the test can tell**: a slice whose act sends a command is
  `Command`; one whose act is a read (below) and none a command is `Query`; one that only asserts a
  read model is `View`. `Automation` and `Translation` need a trigger the test does not express, so
  they stay null unless `Pattern` declares them — a wrong pattern miscolours the canvas, a null one
  does not.
- **A read-only act is a Query slice (bobcat#501, JasperFx 2.85.0's `SlicePattern.Query`).** A vocabulary
  method with `[EventModelTrigger(TriggerKind.Http, HttpMethod = "GET"/"HEAD")]`, a `ReadModel` role and no
  `Command` role is a read act (`ProjectedRoles.isReadTrigger` → `Reading.ReadAct`); the LAST act — command or
  read — is the test's act. **Inferred, no marker**: the trigger attribute is what separates a read act from an
  assertion (`ThenReadModel<T>` carries the same role and no trigger), and the verb separates it from a write, so a
  `SliceRole.QueryResult` or `Query = true` would restate what is already there. `patternOf`: declared → act
  command → `ReadActs` → any command → read model. On a Query `emitSlice` writes no `CommandType` (an arranging
  command is not its act), no `ReadModelTypes`, no `AggregateTypes`, no `ConsumedEvents`, and folds read models
  (the read act's type, asserted read models) into `ReadsFrom` beside any `GivenContext` (#502), so `ReadModelRead`
  links the read model's View to the query. **A stream arranged with plain `GivenEvents` stamps nothing on a
  Query** — it is setup feeding a projection, not a read; an earlier cut put it in `ReadsFrom`, which linked every
  slice producing `Account` to the transactions query. A read act's trigger (`TriggerFromRead`) is
  replaced by any command act's and stamped only on a Query. The manifest skips `inferCommand` for a read act.
  Wolverine derives GET chains as View until wolverine#4975, so BankAccountES's two GET slices merge as View with
  a Pattern `SourceDisagreement` — pinned in `EventModel.cs`, filtered out of the "exactly one planted
  disagreement" count by `isQueryDerivedAsView`.
- **Projected specs feed the model, and the identity has three copies.** `MarkerCommentSpecs` reads
  the tests and `EventModelEmitter.Collect` folds them into one slice dictionary; a test with
  neither a binding nor roles contributes nothing. The identity is `{derived feature title}/{derived
  scenario title}`, derived by `Bobcat.Generators.MarkerSpecNaming` at compile time and
  `Bobcat.MarkerSpecNaming` (through `MarkerStepRun`) at run time.
  **`MarkerSpecNamingAgreementTests` pins the two together** — that string is looked up by
  `DeclaredSteps.For(Uid)` AND published on `scenario_finished`, so a divergence costs a projected
  suite both its rendered steps and its place on the model, silently. The rule: `[BobcatFeature]`'s
  title verbatim when present; else strip one `Specification`/`Specs`/`Spec`/`Fixture` suffix, read
  underscores as spaces (dropping empty segments), else split PascalCase. Scenario titles use the
  same reading of the method name, unless `[BobcatSpec(Name = "…")]` states it.
  `ProjectedSpecNaming.RoundTrips` in `Bobcat.EventModel` is a third copy of the scenario half —
  that assembly references neither — pinned by `ProjectedSpecNamingAgreementTests`.
- **A projected spec's Event Model ROLES come off its vocabulary (bobcat#462, #463).** Scenario
  vocabulary methods carry `[EventModelRole(SliceRole.X, nameof(param or TParam))]` (and the act
  `[EventModelTrigger(TriggerKind.Http, Route = nameof(route))]`, or `RouteName = nameof(routeName)`), both in core
  (`src/Bobcat/EventModel/EventModelRoleAttribute.cs`). `ProjectedRoles.Read` walks a test method's
  invocations through `IInvocationOperation` and reads them off the resolved symbol — **from
  metadata, never by method name**, because the vocabulary (`WolverineScenario`) lives in another
  repository and a rename there must not silently stop the stamping. `MarkerCommentSpecs.Extract`
  stores the reading on `MarkedScenario.Roles`; `EventModelEmitter.Collect` folds it into the
  `SliceModel` lists that `patternOf` / `emitSlice` read.
  - **The rules:** the act is the LAST `Command` call or read act (#464, #501), and its trigger is read only off
    that call; `ArrangedEvent` → `ArrangedEvents` → `ConsumedEvents` only on a View slice (#297);
    `Document` → resolved types only (a document is not an aggregate, event or read model, so it
    gets no element on the canvas); `Context` → `SliceModel.ReadsFrom` → `ReadsFrom`, on any pattern
    and never `AggregateTypes` (#502: `GivenContext<Client>` for a stream the act only looks up, written
    only when non-empty); an argument whose type is not knowable (`object`, interface,
    rows, `null`) stamps nothing. `Specified<T>`, `ExpectedValue<T>` and `TaggedValue` are unwrapped
    by metadata name — Bobcat's own types, so the same-repo contract.
  - **A test with roles and no `[BobcatSlice]` lands on a slice named after its feature.**
  - `Specifications[].ResolvedTypes` (#107's join target) is filled here, and the same list
    rides `SpecificationBindingDescriptor.ResolvedTypes` from `SpecificationManifest` — gated on the
    property existing in the consumer's JasperFx. The manifest prefers the role-stamped act over its
    name-based `inferCommand`, which stays as the fallback for a vocabulary with no roles.
  - **`TriggeredBy` on `[BobcatSlice]` / `[BobcatSpec]` is the trigger label** (#463), read like
    `Pending` rather than as a tag (no second copy of `GeneratorSliceTags` to keep in step). Method
    beats class regardless of fold order (#258, via `TriggerLabelDeclaredOnScenario`); a method
    attribute that sets only `TriggeredBy` keeps the class's slice (and the manifest agrees,
    `onlyTriggeredBy`). `TriggerKind` is deliberately NOT declarable there — upstream says it is
    derived, never declared.
  - **BOBCAT034** (error): a `From`/`Route`/`RouteName` naming no parameter, reported where the vocabulary is
    compiled. `ProjectedEventModelRoleTests` pins all of it, including a vocabulary compiled into a
    separate assembly (metadata path).
  - **The act's route (bobcat#500, `TriggerRoutes`).** A constant `Route` is stamped as written. A constant
    `RouteName` (the second trigger argument, `RouteName = nameof(routeName)`) resolves to the template of
    the one endpoint attribute declaring it. An interpolated (or `+`-concatenated) `Route` takes the template
    of the **one** endpoint whose literal segments match, with a parameter at every hole and a verb that
    agrees when both are known. With no single match it is rebuilt from the interpolation (identifier → its
    name, member access → last member, else `{value}`, repeats numbered) and marked reconstructed;
    `adoptRoute` ranks read/resolved > reconstructed > unknown, order-independent. Endpoints are matched
    **by attribute shape and metadata name, never a package reference**: anything deriving from
    `Wolverine.Http.WolverineHttpMethodAttribute` (`RouteName`, class `[RoutePrefix]` applied as Wolverine's
    `RoutePrefixPolicy` does) and MVC's `Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute` /
    `Microsoft.AspNetCore.Mvc.RouteAttribute` (`Name`, controller `[Route]` + `[controller]`/`[action]`).
    The index is built once per `Compilation` (a `ConditionalWeakTable`) from source plus only those
    references that reference `Wolverine.Http` or `Microsoft.AspNetCore.Mvc.Core`. Minimal API
    `.WithName` and Wolverine's namespace/global prefixes are run-time facts and are not resolved.
    **The route name rides `PublisherOrigin.HttpRouteName`** (JasperFx 2.85.0), next to the route it resolved
    to (`adoptRoute` carries it with the route). An unresolved name is stamped on an origin with no route and
    `Label = "PUT (route name X)"`; `Label` still never carries a name beside a route, because it takes part in
    the `TriggerOrigin` merge. JasperFx merges the name on its own (a known name fills in; two differ → hotspot).
  - **BOBCAT038** (info): the act's `Route`/`RouteName` can't be turned into a route — a helper call or a
    variable, an unknown or ambiguous route name, or none passed. Reported for the act only, at the
    argument, through `MarkedProblem` ("UnresolvedRoute"). `TriggerRouteTests` pins every path, including a
    route name resolved from a referenced assembly's metadata; `samples/BankAccountES`'s `update_a_client`
    addresses its endpoint by route name and `EventModel.cs` checks the specs' own route agrees with
    Wolverine's.
- **`[BobcatSpec(…, Pending = true)]` is a pending specification (issue #404)** — a
  `HotspotDescriptor.PendingSpecification` (jasperfx#689) that `SpecIdentityAudit` reads as
  **joined, not drift**. Without it a scaffolded skeleton *threw* and a pending spec was
  indistinguishable from a failing one. `EventModelEmitter.Collect` routes the identity to
  `PendingSpecifications` instead of `Specifications` — both put it on the slice, which is what
  keeps it joined (not an orphan, and its slice not a hole) while stopping a stub from looking like
  evidence.
  - **Skipped, never swallowed.** `Pending = true` sets `FactAttribute.Skip`, so every runner and
    IDE reports it the way it reports any skip: nobody ran it. Running the body and absorbing the
    failure was the alternative and is exactly how red gets laundered into green — the same
    guardrail as the ledger's refusal to feed proposals back into a policy. An explicit `Skip`
    wins in either written order, which is two tests because property initializers run in written
    order.
  - **"No steps" is NOT read as pending.** A projected test with no marker comments says plenty, and
    **33 of this repo's own 41 projected samples declare no marker steps at all**. Inferring it
    would have turned most of a working suite into open questions.
  - **On `[BobcatSpec]` only, not on `[BobcatSlice]`.** Putting it on the slice attribute would let
    a TUnit suite write it today, but that attribute is not the test: it would mint the hotspot
    while the body still ran and still failed — the exact state #404 exists to end. A partial
    capability that looks complete is worse than an absent one.
  - **Carried on `MarkedScenario.Pending` with its own reader, not as a slice tag.** Pending is a
    property of one specification; the tag vocabulary is slice grouping, and routing it through
    `GeneratorSliceTags` would carry something no slice has an opinion about.
  - A pending spec **stays in the spec manifest and in `--list-tests`** — xUnit discovers a skipped
    test like any other.

### Runner adapters (`Bobcat.Xunit`, `Bobcat.TUnit`, `Bobcat.NUnit`, `Bobcat.MSTest`)

- **`[BobcatSpec]` is the one-attribute projected spec, and it is three facts rather than one
  (issue #403).** `Bobcat.Xunit.BobcatSpecAttribute` IS the `[Fact]`, opens the scenario recording
  `[BobcatScenario]` opens, and carries the `[BobcatSlice]` settings — so
  `[BobcatSpec(typeof(ConfirmAppointment))]` replaces three attributes, and **BOBCAT028 becomes
  impossible by construction** because the attribute that claims the slice is the attribute that
  opens the recording.
  - **It can be both a Fact and a bracket because xUnit v3 collects hooks by the
    `IBeforeAfterTestAttribute` INTERFACE**, not only from the `BeforeAfterTestAttribute` base
    class. The two bases are siblings — both derive straight from `Attribute` — so inheriting from
    both was never an option, and this is the only shape that collapses the pair. Undocumented
    enough that `BobcatSpecAttributeTests` pins it against the real runner. `XunitScenarioBracket`
    exists so the two attributes do not each own a copy of the bracket, the gathered-wrong rethrow
    included.
  - **The `[CallerFilePath]`/`[CallerLineNumber]` pair is re-declared and forwarded, and that is
    load-bearing.** `FactAttribute`'s only constructor takes it and the compiler fills it at the
    call site it SEES, so a subclass calling `base()` without re-declaring them hands over its own
    file and the line of that `base` call — every test in a suite reporting one source location,
    with IDE test navigation landing on the attribute. Measured both ways before being written.
    The attribute is **unsealed** (`[PostgresFact]` is the shape to expect) and a subclass inherits
    the same obligation.
  - **The generator needed teaching in three separate places, because it matches by simple name and
    never sees a base type**: `IsTestMethod` ("Fact"/"Theory"/"Test"/"TestCase"/**"BobcatSpec"**),
    `HasScenarioAttribute` (now `ScenarioOpeningAttributes`), and `sliceTags`. Missing any one
    fails differently — the method is skipped entirely, or BOBCAT028 fires at a suite that records
    perfectly well, or the slice goes unbound.
  - **`sliceTags` reads BOTH attributes rather than preferring one**, and an unnamed `typeof(…)`
    argument is the positional `SliceType`. They are the same settings under two spellings, so a
    method carrying both is one binding stated twice and the existing BOBCAT023 rule already
    answers a disagreement: refuse, because one is wrong and no silent winner is right. Preferring
    one would make the loser's slice vanish without a word. BOBCAT024 now names the attribute it
    fired on.
  - **TUnit's equivalent is deliberately a follow-up** (the issue says so): this subclasses
    `Xunit.FactAttribute`. `[BobcatScenario]` stays for the class-level opt-in and for suites
    already written against it. `Bobcat.Xunit.Samples/Specs/OneAttributeSpecs.cs` keeps one class
    in each form, which is what makes
    `ProjectedSpecIdentityEndToEndTests.a_projected_listing_covers_every_specification…` a real guard:
    the generator recognises the attribute by name and xUnit by base class, two independent
    mechanisms, and that test asserts the two counts agree.
- **A projected run's bracket is per REQUEST, not per process (issue #402).**
  `MarkerStepRun.OpenRun(mode)` / `CloseRun()` are the seam; `Bobcat.Xunit` ships an
  `ITestSessionLifetimeHandler` (`BobcatSessionLifetime`) that drives them. Two run requests in one
  live process now publish two `run_started`, two `RunId`s and two `run_finished`, each command's
  scenarios under its own run — which is #393's requirement in as many words, and the absence of
  `run_finished` was #195's definition of a wedged run.
  - **A test SESSION is a run request, and that is measured on MTP 1.9.1.** Three
    `testing/runTests` requests into one live process fire the handler three times with three
    distinct `SessionUid`s, each properly bracketed; a one-shot direct run fires it once; and
    **discovery opens no session at all**, which is what keeps `--list-tests` from putting an empty
    card on the board.
  - **Registration is half C# and half MSBuild.** The platform generates a
    `SelfRegisteredExtensions` class calling `AddExtensions` on every type a
    `TestingPlatformBuilderHook` item names, so `Bobcat.Xunit` ships
    `buildTransitive/Bobcat.Xunit.props` — and **in-repo projects declare the item themselves**,
    because a `ProjectReference` takes no build assets. **Except the samples (bobcat#484):**
    `samples/Directory.Build.props` `<Import>`s `Bobcat.Generators.props` and `Bobcat.Xunit.props`
    straight from `src/*/buildTransitive/` for every `Tests` project, so a sample is configured
    exactly like a package consumer and no `Tests.csproj` restates either setting.
  - **A missing interceptor opt-in can no longer pass vacuously (bobcat#484).** Without
    `Bobcat.Generated` in `InterceptorsNamespaces` the generator emits no interceptors and reports
    **BOBCAT037** (error) once, at the first call site, read from the same parse option the compiler
    enforces (it was a CS9137 per call site before). At run time, a `SpecAssert` wrong with a scenario
    open and **no step** open (an un-intercepted helper, or no generator at all) goes on
    `Recording.SteplessWrongs` and fails the scenario through `GatheredFailures`; it used to be
    dropped. A *passing* check outside a step stays silent.
  - **A step the interceptor cannot reach is BOBCAT039 (bobcat#535).** The interceptor is a
    file-local static class, so it can only call what the assembly can: a `protected` or `private`
    helper (or one on a private nested type) used to be a CS0122 per call site in the generated file.
    Now it is one error at the helper, read with `Compilation.IsSymbolAccessibleWithin(method, assembly)`,
    and its calls get no interceptor. An error for BOBCAT037's reason: run unintercepted, it opens no step.
  - **`Bobcat.Xunit` takes a `Microsoft.Testing.Platform` reference; `Bobcat.TUnit` deliberately
    does NOT.** `xunit.v3.extensibility.core` depends only on `xunit.v3.common`, so this is the
    first platform reference either adapter has had — and it costs an xUnit consumer nothing, since
    such a consumer already resolves the platform through `xunit.v3` at the same pinned 2.5.1.
    `TUnit.Core` depends on no test platform at all, which keeps that package free of a platform
    choice that belongs to the consumer's own `TUnit.Engine`. **It gets its per-request bracket
    from TUnit.Core instead (bobcat#470):** `BobcatScenarioAttribute` is also an
    `ILastTestInTestSessionEventReceiver` and closes the run once the session's last scenario has
    ended (an open-scenario count, because parallel tests give no ordering promise), so two warm
    commands into one live TUnit host are two runs. The reasoning lives in `Bobcat.TUnit.csproj`.
  - **`Bobcat.TUnit` calls `context.AddAsyncLocalValues()` in `OnTestStart` (bobcat#470).** TUnit
    1.74.0 does not flow AsyncLocal values set in a receiver into the test or the end receiver
    unless asked. Without it the scenario opened, `OnTestEnd` found no `ScenarioRecorder.Current`,
    and **no verdict was ever published** (only `scenario_started`). Nothing in this repository could
    run a real TUnit host before #470, so it went unseen; `TUnitResidentSuiteTests` now would catch it.
  - **`Bobcat.NUnit` and `Bobcat.MSTest` (issues #385/#386) follow `Bobcat.TUnit`: no platform
    reference.** Their *test projects* run on NUnit3TestAdapter 5.2.0 and MSTest.TestAdapter 3.11.1,
    the last releases built on MTP 1.9.x (6.0 / 4.0 move to 2.x) — `src/Directory.Packages.props`
    says so. Two decisions worth not re-deriving:
    - **MSTest's bracket is a `[GlobalTestInitialize]`/`[GlobalTestCleanup]` pair (MSTest 3.10+),
      shipped as SOURCE in `buildTransitive/BobcatMSTestHooks.cs`**, and `[BobcatScenario]` there is
      a plain marker the hooks look for. MSTest only finds global hooks in the test assembly, and
      the alternatives the issue listed both fail its "add one attribute" rule (a
      `TestMethodAttribute` subclass replaces `[TestMethod]`; `[TestCleanup]` needs a base class).
      **`Bobcat.MSTest` references no MSTest assembly**: MSTest 4 renamed them
      (`Microsoft.VisualStudio.TestPlatform.TestFramework` → `MSTest.TestFramework`) and turned
      `TestMethodAttribute.Execute` into `ExecuteAsync`, so a type compiled against one major does
      not load under the other. The hook source compiles against the consumer's MSTest and passes
      plain values (the outcome by *name*). Verified as a package consumer on MSTest 4.5.1 out of
      tree, since this repo cannot run an MTP 2.x host.
    - **Both test projects prove the failing cases in a child process** (`Probes.cs`): a probe
      class kept out of the normal run (`[Explicit]` for NUnit, a `ConditionBaseAttribute` for
      MSTest) is re-run by the real host with a filter, and a `ScenarioCompleted` listener writes
      what was published. That is how "the outcome is populated in AfterTest / GlobalTestCleanup"
      and "a throw from there fails the test" are tested rather than assumed. Note `SpecAssert`
      records onto the **step in progress**; a wrong with no step open goes on `SteplessWrongs`
      (above).
    - **Run by identity refuses both** (`SpecFilterArguments`), like TUnit — but unlike TUnit their
      filter spelling *could* be verified here, since both run on MTP 1.9. That is the follow-up.
  - **`ProcessExit` stays as the backstop and now drains unconditionally.** `CloseRun` is
    idempotent, so a bracket the session hook already closed is found closed — but the old code
    returned early when it had nothing to close, which after this refactor would have let the last
    `run_finished` of every warm process die in the publisher's channel. **`CloseRun` keeps the
    sink** (disposing it would stop the pump for the next request) and resets `_info`/counts so the
    next bracket mints a fresh `RunId` with that request's own tallies; only process exit disposes.
    Ordering needs no flush — one bounded channel, drained FIFO.
  - **The command id is read per request** (`OpenRun` re-runs `MonitorRunInfo.Discover`), while the
    sink is resolved once per process (`_sinkResolved`) so no command pays for a console handshake.
    **For a warm out-of-process child the variable still cannot change**, and that is the finding
    this uncovered: a child's environment is fixed at launch, and MTP 1.9.1 has no per-request
    metadata slot — the `runId` on `testing/runTests` is the client's own and is **not** the
    `SessionUid` the handler receives (measured; unrelated GUIDs). A file whose path is fixed at
    launch, the way `BOBCAT_LIST_SPECS` works in the other direction, is the answer —
    `BOBCAT_RUN_COMMAND_FILE`, under the resident runner's warm suite.

### Tables, sets and objects (`src/Bobcat/Runtime/`)

Public statics, so any projected grammar — a plain class — reaches one engine:

| | |
|---|---|
| `TableRunner.Run(this, nameof(rowMethod), table)` | Storyteller's `AsTable(...).Before(...).After(...)`, where the hooks are the lines either side of the call |
| `TableRunner.BuildRows<T>(table)` | Storyteller's `CreateNewObject<T>` |
| `SetVerificationComparer.Verify(actual, expected, keyColumns:, ordered:, column:)` | set verification |
| `PropertyCells.Verify(subject, expected)` | Storyteller's `VerifyObject` / `CheckPropertyGrammar` (issue #395) |
| `ObjectSetVerification.Verify(...)` | sets of objects and partial objects (below) |

**`TableRunner` is a decision table when it has outputs.** Columns bind to the row method's
parameters by name (`[Header]` title, else the name, case-insensitive); an optional parameter's
column may be left out; cells convert through `CellValues`. **Each `out` parameter is an output
column (bobcat#483)**, compared after the call exactly as a returned value is; a returned value is
compared against the one column no parameter names, in or out — with none, or more than one, it is
not checked. A row that throws is a `row-error` cell and the remaining rows still run
(`DecisionTableComparer.IsRowFailure`); `SpecCriticalException`, `SpecCatastrophicException` and
cancellation are not row failures. Naming the row method by string is a bounded softening of "no
reflection", beside `RecordBuilding` and the store conventions.

A column naming no property is an `invalid` cell **at run time**, unless the table is a constant the
generator can check — see BOBCAT032 below.

`PropertyCells` is general-purpose; it is **not** a set verification of one row, because a set
matches by key columns so one wrong value becomes a missing row beside an extra one, where here the
subject is known and the columns *are* the claim.

**A column may be a dotted path into a nested object (issue #411).** `Customer.Address.City` resolves
left to right over public instance properties, with `[Header]` titling applied **per segment** so a
renamed property stays addressable at every depth. Put in the shared lookup rather than in any one
grammar because several callers share it — `PropertyCells.Verify` and the Wolverine side's event and
HTTP-response assertions (wolverine#4835/#4836) among them — and solving it in one would leave the
others with a different rule for what a column name means. Three decisions worth keeping:

- **A missing segment is `invalid`; a null partway along is `failed`.** The first is the
  specification asking about something that does not exist, decidable without running anything; the
  second is the subject disagreeing, decidable only at run time. Collapsing them would make a legal
  spec over an empty graph read as an authoring mistake. A null at the *end* of a path is an
  ordinary value — `NULL` is a cell expression a spec may assert.
- **`invalid` names what is there at the depth that failed** — `no 'Town' on Address — it has City,
  Line1, zip` — not the subject's own top-level properties, which is what a naive extension prints
  and is the least useful half of the sentence.
- **Collection indexers are refused with a message, not supported.** `Items[0].Sku` is a set
  verification's question: a set matches by key columns, where a path into a collection makes one
  wrong value read as a missing row beside an extra one — the exact confusion the paragraph above
  warns about. Depth is capped (`PropertyCells.MaxDepth`) so a cyclic graph cannot hang a comparison.

### Partial objects: `Specify<T>()` and partial tables (`src/Bobcat/Partial/`, bobcat#416/#417)

A spec names only the members it is about. A **partial object** (`IPartialObject`) is a type plus
an ordered list of `SpecifiedValue(Path, Value, IsText)` — typed values from code, or cell text from
a table, read later with `CellValues.Read` so `NULL`/`EMPTY`/relative times mean what they mean in
every other table. Three ways to write one:

- `Specify<T>().With(x => x.Prop, value)` (`using static Bobcat.Specifications;`). Nested:
  `.With(x => x.Address.City, …)` or `.With(x => x.Address, Specify<Address>()…)`.
  `.With(nameof(T.Prop), value)` for a string path. **Refactor friendly by construction** — teach
  this, and `nameof` in table headers, over bare strings.
- `PartialObjects.FromTable(type, table)`: a horizontal table is one object per row; a vertical `|
  field | value |` table (`PartialObjects.IsVertical`) is exactly one. **A blank cell is "not
  specified"** (so one table can carry several types under a union header); `EMPTY` is the empty
  string.
- `PartialObjects.FromCells(type, row)`.

**Building** (`ObjectConstruction`) — constructor first, then members:

1. Of the public constructors that can reach every specified member (by a parameter, or by a
   settable/`init` member afterwards), take the one binding the most, then the one leaving fewest
   parameters to fill, then the shortest. Structs also get their implicit constructor.
2. Unbound parameters take their C# default, else the fill policy.
3. Remaining specified members are assigned through setters or `init` accessors (reflection may call
   `init`) — the gap `RecordBuilding` has: it sets nothing once a constructor binds.
4. A `required` member nothing set, and a non-nullable reference member the type's own initializers
   left null, get the fill policy's value. **A member the type initialized itself is left alone** —
   that default is the author's.
5. Spec defects throw `SpecCriticalException` naming the member: matches nothing (lists what the
   type has), read-only and taken by no constructor, specified both whole and by members, an index
   path.

**Unspecified values go through `IUnspecifiedValues`**; `PredictableValues` is the default: null
where nullable (NRT annotations; *unknown* — F#, oblivious code — counts as non-nullable), `""`,
empty collections, `None`, **a fresh `Guid`** (`Guid.Empty` collides identities), the Bobcat clock's
now, an enum's first value, `default` otherwise; any other reference type is built empty, and a
type already under construction is left null so a cycle terminates. **The policy is an open
design question (bobcat#421: Bogus/AutoBogus vs Storyteller-style declared defaults)** — keep it
behind the seam.

**F# without FSharp.Core** (`FSharpShapes`, by type name): records and `[<CLIMutable>]` go through
the constructor; `option`/`voption` (blank or `NULL` cell = `None`; an inner value is wrapped in
`Some`); `list`/`Set` (comma-separated cell or any enumerable); `Map` (empty only); a single-case
union with one field (`OrderId of Guid`) is built from its wrapped value. Multi-case unions are not
supported yet. `src/Bobcat.Tests.FSharpTypes` holds real compiler-output shapes plus F#-authored
`Specify` calls — test new shapes there, never with C# imitations.

**Matching (#418, `PartialMatching`, `ObjectSetVerification`).** `PartialMatching.Differences(actual,
expected)` judges a partial object on **only** the members it names (cell text through `CellCheck`,
typed values structurally), an `IExpectedValue` (`Expect.Value(x).Ignoring(...)`) on everything but
its ignored members, a plain object on everything. `ObjectComparison`/`Expect`/`ExpectedValue` live
here now (moved from WolverineFx.Bobcat, wolverine#4835) — one comparison. F# options and single-case
unions compare by what they hold (`ObjectComparison.Unwrap`). `ObjectSetVerification.Verify(actual,
expected, noun, SetMode)`: `Ordered` (exact + ORDER), `AnyOrder` (exact), `Contains` (no EXTRA rows,
others allowed); `Absent(actual, forbidden, noun)` for "not emitted" (a `Type`, or a partial = only
matching ones). **Exact pairs are a maximum matching (Kuhn), not greedy** — a broad expectation
written first must not steal the event a narrow one needs, and an ordered comparison seeds that
matching with the longest pairing that keeps the specification's order (#476), so ORDER is reported
only when no pairing avoids it. **Pass 2 — the same type with different values, a FAIL rather than a
MISSING beside an EXTRA — only pairs items that HAVE members to disagree about**: pairing two scalars
says "expected rush, was vip" about two items with nothing to do with each other. A matched partial
row shows only the specified members of the actual. `PropertyCells.Verify(subject, IPartialObject)`
gives a one-row grid of just the specified columns, and a collection member's cell carries every
difference *inside* it (`PartialMatching.Owns`) — keyed on the member path alone, a nested difference
reached no cell and a failed comparison rendered green.

### A collection member is a set, a dictionary member is keyed (bobcat#456)

**`ObjectComparison.Compare` is recursive**, not flatten-both-graphs-and-match-leaf-paths, and when it
meets a collection it delegates the matching to `ObjectSetVerification.Pair` — extracted from that
type's own grid so there is **one matching and two renderings**. Before this, a collection member was
compared position by position, so a reordered one reported every element as wrong: the exact failure
`ObjectSetVerification` exists to avoid, unreachable from inside an object. `Leaves` stays as the
flattener for rendering one graph; the comparison no longer uses it.

- **Unordered by default; strict ordering is opt in, case by case** (Jeremy, 2026-10-12). A bare
  collection member is `AnyOrder`. Most collection members have an order the system happens to produce
  rather than one the specification is about. `Expect.Exactly(…)` claims the order at the member level,
  `Expect.Value(x).InOrder(e => e.Lines)` for one member of a whole object.
- **`Expect`** carries the vocabulary beside `Expect.Value`: `Exactly` / `InAnyOrder` / `Containing` /
  `NoneOf` / `NoItems`, plus **`Rows(table, mode)`** and **`NoRowsLike(table)`** for a child collection
  written as a grid. The table forms are **named apart deliberately**: `StepTable` converts implicitly
  from a string, so an `Expect.Containing(StepTable)` overload beats `params object[]` for a single
  string argument and `Expect.Containing("vip")` over a collection of strings silently became a table
  with no rows — an expectation that asserts nothing and passes. A test caught it.
- An item is **any expectation** — a whole object, a `Specified<T>`, an `Expect.Value`, a bare `Type`.
  A null element becomes `NullItem` so a set verification (which reads an item's runtime type) can pair
  it. `ExpectedItems.ItemsFor(elementType)` materialises a table form; the element type comes from the
  member via `With<TValue>`, falling back to the actual collection's at compare time.
- **A nested disagreement names the item that disagreed** — `Lines[1].Quantity`, at the position the
  actual item is at — and an expectation nothing satisfied names the **member**, with no index, because
  it was not found anywhere. `ValueDifference` and `ObjectComparison.Leaf` carry a **trailing optional
  `Note`** (MISSING / EXTRA / out of order / present-when-forbidden / wrong type), the same move as
  `StepCell`'s trailing comparison word. `ValueDifferences` renders a list of them in one place, capped
  at **ten** with "…and N more" — a reordered fifty-element collection is a hundred disagreements — and
  the cap is at the rendering, so a programmatic caller still gets every one.
- **A dictionary member is keyed** (`Headers[accept]`), so two dictionaries holding the same entries
  agree whatever order they enumerate in and a key one side lacks reads as that key. Only a real
  dictionary: a `List<KeyValuePair<,>>` is ordered and may hold one key twice, so reading it by key
  would lose its order and silently drop a duplicate's leaves.
- **A member whose runtime type is not the expected one is one difference** naming both types, and the
  walk stops there — it was a cascade of leaves the actual has and the expectation does not, with the
  type claim never stated. The top level already requires the exact type, so the silent one at depth
  was the odd case out. Checked **after** the collection arms, where a `List<T>` and a `T[]` are the
  same claim. Naming members is unaffected: a dotted path asks about a member, not about which type
  carries it.
- **`ComparisonRules`** carries ignored paths, per-path `Tolerance` and the claimed-order paths, so the
  comparison's parameter does not grow a sibling per refinement; `IExpectedValue.Rules` is a defaulted
  member over `IgnoredPaths`. `Expect.Value(x).Approximately(m, 0.01m)` / `.Within(m, 2.Seconds())`
  render the `±` note a table cell already shows for `CheckOptions.Tolerance`. Slack on a member that
  cannot have any is a `SpecCriticalException` naming it — a specification asking for slack on a value
  that has none is a spec defect, and comparing exactly instead would hide it. **A rule names a path
  from the object compared and does not reach inside a collection's elements** (an element has no path
  a specification writes); an element that needs its own slack is its own `Expect.Value`.
- **The ancestor set is `[ThreadStatic]`, owned by the outermost call.** A collection's items are
  compared through `PartialMatching`, which re-enters `ObjectComparison`, so a set each call owned
  privately would be a fresh one per element and a cycle **through a collection** would not terminate.
  `MaxDepth = 8` stays as the backstop.
- **`ObjectComparison.Format` overrides a type's own `ToString` only where it has nothing of its own
  to say.** A collection's gives its type name where a reader wants its items, and a record's
  generated one walks the graph and recurses forever on a cycle (`PrintMembers` gives it away) — but a
  hand-written `ToString` is how the type wants to be read, which the first cut broke for the tag list.
- **Shouldly's `ShouldBe` over a collection stays ordered** (`MemberChecks.Read`), with
  `ignoreOrder: true` as the unordered form: `ShouldBe` is Shouldly's word and its own meaning over an
  `IEnumerable` is ordered. What Bobcat adds is member by member. `ShouldBe(x, tolerance)` still falls
  through to a `MemberCheck`. A collection expectation **cannot be built**, the same refusal a member
  check already gets.
- User docs: `docs/expected-objects.md`, which is also #452's home for the member-check C# 14
  requirement. Worked corpus: `Bobcat.Projected.Acceptance.Tests/ExpectedObjectTests`, asserting on the
  **grid** rather than the difference list, because #456 was about what a reader could tell.

**`RecordBuilding.Build`** (`src/Bobcat/CritterStack/`, public) is a thin call into this engine, and
every build is partial (#419). It fills with `DefaultValues` (`default(T)`, the #241 rule) — *not*
`PredictableValues` — until #421 decides. A blank cell is not specified; a column matching nothing
on the type still fails by name.

### BOBCAT032: table columns checked at compile time (`TableColumns`, `TableCallChecks`, bobcat#415/#420)

A **constant** table (literal, raw string, or a constant interpolated string such as
`$"| {nameof(Order.Total)} |"` — evaluated with `SemanticModel.GetConstantValue`) whose columns do
not name members of the type it is about is a build **error**. Recognized by call shape, not by
attribute, so a downstream wrapper (Wolverine's `Verify(state, table)`) is covered for free:

| Call shape | Type checked | Rule (`TableColumns.ColumnRule`) |
|---|---|---|
| `(object subject, StepTable expected)` — `PropertyCells.Verify`, any such pair | subject's static type | `Property`: properties by `[Header]` title (which REPLACES the name), dotted |
| `(IEnumerable actual, StepTable expected)` — `SetVerificationComparer.Verify`, any such pair | element type (scalar elements skipped) | `Set`: same titles, top level only; constant `keyColumns` checked too |
| `PartialObjects.FromTable(typeof(T), table)` | `T` | `Partial`: property/field by name or title, or a public constructor parameter, dotted |
| `Specified<T>.With("path", value)` | `T` | `Partial` |
| `Expect.Rows(table)` / `Expect.NoRowsLike(table)` (bobcat#456) | the **member's** element type, from the enclosing `With<TValue>`'s inferred `TValue` | `Partial`; scalar elements skipped |

Vertical `| field | value |` tables check the first-column values. Indexers are refused, depth is 8.
`Expect.Rows` is the one shape whose type is **not on the call** — the whole point of the table form is
that the author never repeats the element type — so it reads the enclosing `With`; anywhere else it is
skipped, the same degradation as a non-constant table.

**Skipped (left to the run-time `invalid` cell):** a non-constant table; a subject typed `object`,
an interface, abstract, or a type parameter; and — for the two RUNTIME-type readers (property
check, set) — an **open class**: one that is not a struct, sealed, or a record and that some type
in the compilation or its non-framework references derives from (#415's option 1, chosen pending
the user's call), because a subclass may legitimately carry the column. The same rule applies to
each nested segment of a dotted path. A partial object builds exactly `T`, so it is always checked.

**Rendering (#420):** a projected (Shouldly) assertion's sentence shows `nameof(Foo.Bar)` and constant
interpolations by value (`"Bar"`), and a `Specify<T>().With(x => x.Bar, v)[.Build()]` argument as
`T(Bar: v)` (`ArgumentText`). Those sentences are the only place the generator renders C# argument
source; `[BobcatStep]` placeholders and marker comments already show run-time values/prose.

### Engine (`src/Bobcat/Engine/`)
- **A cell says what it compared (`Engine/Comparison.cs`, issue #384 part B).** `CellResult` was
  equality-shaped by construction — `Expected` + `Actual`, rendered `expected 'x', got 'y'` — so a
  cell produced by a non-equality assertion **stated something false**: Shouldly interception shipped
  cells with no comparison, and `ShouldBeGreaterThan(10)` against 3 rendered `expected '10', got '3'`
  where 10 is the bound. The *sentence* was right, because the dialect writes the comparison into the
  step text, so the console read correctly and only a consumer rendering cells as a grid saw the
  false half. That is why it survived.
  - **The enum is closed, and being closed IS the whitelist** — `Equals` / `NotEquals` /
    `GreaterThan` / `GreaterThanOrEqual` / `LessThan` / `LessThanOrEqual` / `Contains` /
    `StartsWith` / `EndsWith` / `Approximately` / `IsNull` / `IsNotNull` / `IsEmpty` / `IsNotEmpty`.
    An assertion Bobcat has no member for produces **no cell at all** and the step renders as a
    plain line; a free string would let a producer invent a comparison nothing can render or check.
  - **It EXTENDS `CellResult` rather than replacing it**, which was #384's open question. Every
    existing producer is genuinely making an equality claim, so a **null default is honest** and
    reads as `Equals`; `DisplayText`'s legacy constructor is load-bearing for the set-verification
    path and must not be disturbed; and #396 settled that a cell says exactly one thing with its
    *content* fields, where a comparison is not content but a statement about how `Expected` and
    `Actual` relate — orthogonal, so it sits beside them. `WithNote` **carries it**, because without
    that, appending a note turned a `GreaterThan` cell back into an equality claim.
  - **Rendered in one place, `CellResult.derive()`**, which is what the Spectre grid, the inline
    sentence, the JSON report and the wire all already read — so they cannot disagree about what a
    comparison said. #384's acceptance asked for a two-column table only for an all-`Equals` group;
    there is no separate two-column shape in this renderer to switch on, so the faithful reading is
    that each cell states its own, which gives a mixed group per-row rendering by construction and
    an all-equality group byte-identical output.
  - **Acceptance 4 is the overload the generator picks** (`StepInterceptors.emitAssertion`): no
    comparison means the cell-less `Gather`, so the decision is compile-time and the runtime never
    judges whether a cell it was handed is describable.
  - **The interceptor's signature must match exactly, and two shapes did not (issue #410).** Both
    broke the **consumer's** build, in a generated file the author cannot edit, over assertions that
    compile perfectly without Bobcat.
    - **The return type was always written `void`.** `ShouldNotBeNull<T>` and `ShouldBeOfType<T>`
      return `T`, so a statement-level call to either was CS9144. Fixed by emitting the real return
      type, capturing the result inside the `Gather` lambda and handing it back. Returning `default!`
      after a *gathered* failure is safe for a reason that already existed: only a **statement-level**
      call is intercepted (`ProjectedAssertions.StatementOf`), so the value is discarded at the call
      site by definition — `x.ShouldNotBeNull().Name.ShouldBe("a")` consumes the result, is never
      intercepted, and throws as it always did.
    - **An enum default was emitted as its number.** `ExplicitDefaultValue` boxes an enum as its
      *underlying primitive*, so `defaultOf`'s `IsPrimitive` guard caught it before the fallback its
      own comment promised, and `Case caseSensitivity = Case.Insensitive` came out as `0` — CS1750.
      That took out `ShouldContain`, `ShouldStartWith` and `ShouldEndWith` over a string. Emitted as
      a **cast** rather than as `default`, which would be right only for a zero-valued member.
    - **`CanReproduce` is the guard that keeps the next one from reaching a consumer**, extending
      #384's degradation from "no comparison for this name" to "no signature that can match this
      call": by-ref parameters and by-ref returns cannot survive the `Gather` lambda, so no
      interceptor is emitted and the assertion runs as it always did. A lost cell is a small,
      visible loss; a build that will not compile is not.
    - **Why nothing caught any of it, and what now does.** `AssertionComparisonTests` pins the
      dialect's mapping — a pure function from a method *name*, testable without a compilation, which
      is the right design and stays. Nothing tested that a claimed name could be *intercepted*, and
      the halves are in different assemblies, so the only thing that failed was somebody's build.
      `ProjectedAssertionCompilationTests` now generates and **compiles** every claimed name through
      the real generator. Two things the harness needed for that, each a trap in its own right:
      projected assertions are **opt-in per project** (`build_property.BobcatProjectAssertions`), and
      C#'s `interceptors` feature must be enabled for `Bobcat.Generated` — without either, the
      generator emits nothing and a compile assertion passes over an empty file. **It did: the first
      cut of that theory went green on 15 rows having generated nothing at all**, which is why every
      row now asserts the interceptor exists before asserting it compiles.
  - **The Shouldly mapping is a pure function of method name plus has-a-tolerance**
    (`ShouldlyDialect.ComparisonFor`), testable without a compilation — and `ShouldBe(x, 0.01)` is
    `Approximately`, the one case the name alone cannot settle. **The halves are in different
    assemblies** (the netstandard2.0 generator cannot reference the runtime), so a typo would emit
    `Comparison.GreatherThan` and break the *consumer's* build in a generated file; a test checks
    every mapped name against `Enum.GetNames<Comparison>()`.
  - On the wire it is a trailing optional camelCase word on `StepCell`, **null meaning equality** —
    so a consumer that ignores it is exactly as correct as it was.
- **A scenario can report an account of itself, not only a verdict (`Engine/ScenarioReport.cs`,
  issue #408).** `IScenarioReport` is a titled grid of cells describing what the system *did* —
  Storyteller's custom logging (`ISpecContext.Reporting`, `Report.ToHtml()`) answered with the Cell
  model instead of HTML. Built for asynchronous messaging specs, where the evidence that diagnoses
  a failure is a tracked session's envelope record and belongs to no single step.
  - **Scenario-scoped is the whole point.** `SpecReport.For<T>()` accumulates onto
    `ScenarioRecorder.Recording.Reports`, one instance per report type per scenario — which is how
    several grammars append to one table agreeing on nothing but the type, Storyteller's
    `ReporterFor<T>()`. It sits beside `TouchedTypes` on the recording, because a report must reach
    the wire for the same reason run evidence does.
  - **`SpecReport` is static, and that is load-bearing rather than convenient.** A producer in
    another package (`WolverineFx.Bobcat`) must compile **once** and work inside any projected test,
    which has no context object to hand it. So the surface is `AsyncLocal`-backed and ambient
    (`ScenarioReports`), the same answer `ScenarioRecorder.Current`, `BobcatClock` and `SpecAssert`
    already give to the same problem. `BothLanesReportTests` pins the shared producer.
  - **Rows are unjudged `Value` cells (#396), so a log table cannot claim it checked anything.** A
    report that *does* want to judge one row — the envelope that was dead-lettered among a dozen
    that were fine — builds an ordinary judged `CellResult` for it, so a red row inside an
    informational table needs no second mechanism. The grid folds through
    `SetVerificationRender.FromCells` and renders through `CommandLineRenderer.RenderSetVerification`,
    so a report's table and a set verification's table cannot look different, and
    `ShowsStatusColumn` already drops the Status column for an all-unjudged grid.
  - **`ReportVisibility` is `OnFailure` by default and `run --json --verbose` is the only thing that
    lifts it** (decided 2026-10-06). `ScenarioReportVisibility` is the one rule all three surfaces
    ask — console, JSON report and wire — so they cannot disagree about what a run reported, the
    same discipline `CellResult.derive()` follows. The rejected alternative was JSON always carrying
    everything on the argument that an agent reads the archive afterwards and volume there is cheap:
    it makes JSON an exception to the uniform rule, and an agent that wants everything can ask and
    then gets it everywhere at once. `BOBCAT_VERBOSE_REPORTS` is the environment's spelling, read as
    a tri-state through `ProjectedSpecConsole.Setting` so "nobody said" stays distinguishable from
    "somebody said no". An **empty** report is dropped whatever its visibility — a grammar that was
    never exercised legitimately produces one, and an empty grid under a heading says less than no
    heading.
  - **200 rows per report, capped, with "…and N more" — never a silent truncation.**
    `docs/tutorials/agent-friendly-tests.md` records what happens without a cap: a suite dumped a
    698MB tracked-session log into test output, faulted the worker, and turned every unreported test
    indeterminate — a run that read as a crash because something tried to say too much. The cap is
    in `TableReport` rather than left to each producer to remember.
  - **On the wire: `ScenarioFinished.Reports`, trailing and optional**, carrying
    `ScenarioReportInfo` whose cells are `StepCell` — one cell shape on this wire, not a sibling
    record. `MonitorReports.From` is the single projection and applies the visibility rule itself,
    the `StepCells.From` lesson from
    #396: two copies of one projection is what once silently dropped a literal's text.
    Null rather than an empty list when there is nothing, so "reported nothing" and "a publisher too
    old to know about reports" look the same — because they mean the same thing.
  - **One core fix fell out of building it.** `CellResult.WithNote` copied through the *structured*
    constructor and so dropped a legacy-constructed cell's private `_displayText`, leaving
    `DisplayText` to derive the empty string for a plain value cell. That is #396's defect in the
    model rather than in a projection, and it went unnoticed because every existing caller happened
    to pass structured cells. `WithNote` and the new `WithRowIndex` now share one private `copy`,
    and any future "a copy of this cell, but…" belongs there.
- **A projected spec's logs and reports reach the runner's own test output (`SpecOutput`, issue
  #409).** The one place a developer looks when `dotnet test` goes red is the platform's output
  block for that test, and nothing Bobcat knew was reaching it: `ProjectedSpecConsole` deliberately
  stays silent under a captured stream, because a captured stream belongs to whoever captured it.
  - **Core names neither runner's types.** The seam is an `Action<string>` of lines, supplied by the
    adapter — the third thing on the marker-step adapter seam after the two verdict callbacks.
    `Bobcat.Xunit` hands over `TestContext.Current.TestOutputHelper.WriteLine`, which is **ambient
    in xUnit v3** and so costs a `[BobcatSpec]` test no constructor parameter — requiring one would
    spend #403's whole point. `Bobcat.TUnit` hands over `context.OutputWriter.WriteLine`, because
    TUnit has no `ITestOutputHelper` at all; that asymmetry is why the seam is a delegate rather
    than an interface borrowed from one of them.
  - **`AsyncLocal`, because tests run in parallel.** A static sink would send one test's lines to
    another test's output, which is worse than sending them nowhere.
  - **Logs go through as they happen; reports go at the close.** A log line is written immediately,
    so a test that also writes to its own output reads as one stream in source order rather than as
    two blocks. A report is an account of the whole scenario and cannot exist until it closes, so it
    is written from `ScenarioRecorder.ScenarioCompleted` — after the verdict is known, which is what
    the visibility rule needs anyway. The adapters therefore dispose the sink **after**
    `EndScenario`, not before.
  - **`WriteReports` takes the `SpecRender`, not the reports.** `SpecRender.Reports` has already
    applied `ScenarioReportVisibility` and already knows whether the scenario failed, so the output
    block cannot reach a different answer than the console and the JSON report did.
  - **Logs always, reports latched — and that asymmetry is deliberate.** Bobcat does not latch the
    log line, because the platform already attaches per-test output to the result and shows it for
    failures; a second opinion about the same question is how the two disagree, and withholding on a
    pass would leave a *passing* test somebody is debugging with no output. A report is up to 200
    rows a grammar produced whether or not anyone wanted them, so it honours `ReportVisibility`.
    Where Bobcat owns the whole surface — the JSON report — it latches uniformly.
  - **Writing can never fail a test.** Every call is guarded: a runner whose output helper has been
    torn down throws, and a specification that reported something must not go red because the
    reporting channel closed first. The monitor publisher's invariant, applied here.
  - **`TextGrid` is a second renderer, not a reuse of the Spectre one.** `ITestOutputHelper` and
    TUnit's writer are plain text captured by the platform, so markup would arrive as escape codes
    in exactly the place a failure is read. The row that disagreed is marked `<-- FAILED` in text
    rather than coloured, so it is findable by eye in a CI log and by grep — neither of which sees
    an ANSI code. Same model, two outputs, which is the split `SpecRender` exists for.
  - **`PlainSpecText` (`Bobcat.Rendering`) is where the plain-text rendering lives**, and
    `SpecOutput` delegates to it, so there is one copy of the rendering — the lesson `StepCells.From`
    already taught (#396).
- **`StepKind`** / **`FailureLevel`** — drive failure classification.
- **In-flight progress:** `ScenarioRecorder` posts `step_progress`, and `TotalSteps` / `StepNumber` /
  `ScenarioElapsedMs` ride the step events; a long table's progress is its cells-so-far update
  (#387). Details in `docs/monitor-design.md`.
- **Run evidence (#107, #465):** `ScenarioRecorder.RecordTouchedType(Type)` accumulates onto the
  recording and reaches `scenario_finished.TouchedTypes` through `MonitorTouchedTypes` (JasperFx
  `TypeDescriptor`'s three fields, `FullName` joining a design-time
  `SpecificationDescriptor.ResolvedTypes`). Observed, never asserted. Nothing recorded is null, not
  an empty list.
- **Timeline analysis (issue #142, items 1–3 only):** `SuiteTiming.For(recordings)`
  (`Runtime/SuiteTiming.Projected.cs`) is the in-process sibling of the supervisor's `RunTiming` —
  pure computation, rendered as `CommandLineRenderer.RenderTimingSummary` and the `timing` block of
  `JsonRenderer.RenderTiming`. Report, don't act — same guardrail as `RunTiming` and #44's hints.
  Gap ranking, per-step aggregation grouped by *normalized* step text (`NormalizeStepText` folds
  quoted strings/bare numbers to placeholders), and scenarios that assert nothing. **Lifecycle is
  not measured** (`LifecycleMeasured` is false): it belongs to the test runner — `IAsyncLifetime`,
  class and assembly fixtures, TUnit's hooks — and Bobcat neither owns nor sees it; a consumer
  renders "not measured", never a zero (#141's rule). Item 5 (cross-run trends) ships on the
  committed ledger (`TestLedger.Trends()`, `design/ledger-design.md`); item 4 (sleep-shaped
  durations) stays unbuilt — its false-positive question is open, and `[SlowByDesign]` is a
  JasperFx.Testing decision.

### Rendering (`src/Bobcat/Rendering/`)
- **`SpecRender`** — the intermediate model every output reads (`SpecRender.FromRecording`)
- **`CommandLineRenderer`** — Spectre.Console ANSI output; set verifications render as a table with
  per-cell colouring
- **`TextGrid`** / **`PlainSpecText`** — plain text for a platform's captured test output
- **`JsonRenderer`** — the JSON report

### Three-Level Failure Semantics
1. **Assertion** — a gathered wrong (`SpecAssert`, a `[Check]` returning false,
   `SpecAssertionException`) → continue, gathering every failure
2. **Critical** — `SpecCriticalException` or any other exception in a step → abort the scenario
3. **Catastrophic** — `SpecCatastrophicException`: unrecoverable infrastructure (host won't start,
   database gone). Never retried — `DefaultFailurePolicy` and `HintedFailurePolicy` both refuse it

### Resilience (`src/Bobcat/Resilience/`)
The retry policy layer — `FailureLevel` promoted to a decision the supervisor acts on.
Framework-neutral by design, so the same engine drives xUnit, TUnit, NUnit and MSTest workers
(issue #41).

- **`Disposition`** — `Pass` / `FailAndContinue` / `RetryInProcess` / `RetryInFreshProcess` /
  `RetryAfterRecycle(resources…)` / `AbortRun`, each with a human-readable `Reason` that reaches
  the report.
- **`IFailurePolicy`** — `AttemptContext → Disposition?`. **Returning null abstains**, so narrow
  policies compose; `FailurePolicyChain` takes the first non-null and `DefaultFailurePolicy`
  always decides last. Register with `Supervisor.AddFailurePolicy(...)`.
- **`RetryBudget`** — two independent ceilings because they fail differently:
  `MaxAttemptsPerTest` stops one pathological test, `MaxRetriesPerRun` stops a pathological
  *environment*. Default is `RetryBudget.None`, so an unconfigured run behaves exactly as before.
- **`ResilienceTags`** — the trait vocabulary policies read: `Retry=N`, `Isolated=true`,
  `RecycleOnRetry=rabbit,kafka` (a test writes `[Trait("Retry", "2")]`). The #43 spike found traits
  are the only metadata channel that survives every front-end intact, which is why policy keys off
  **traits, not exception types** — tUnit erases exception types entirely on the MTP wire.

**Retries are opt-in.** A failure with no `Retry` trait is never retried, even with a budget
configured. Retrying by default would turn every genuine assertion failure into a slower one and
make flaky indistinguishable from broken.

**Honest reporting ships with it** — `RunOutcome` is `CleanPass` / `PassOnRetry` / `Failed` /
`Aborted`, and `PassOnRetry` is never folded into `CleanPass`. `SupervisorResults.PassedOnRetry`
is the run's flakiness ledger; a pass-on-retry still exits 0 but is reported separately. A
disposition that could not be honoured is recorded in `UnsupportedDispositions` rather than
silently downgraded, so a report never implies a retry that never happened.

#### Recovery hints — author-declared, per failure class (issue #44 layer 1)

A trait says *this test* is unreliable; a hint says **which failure** is unreliable and what fixes
it. Declared on the test class (or method, or assembly), so an assertion failure in the same test
is still reported as the bug it is:

```csharp
[ClearsOnRetry(typeof(TimeoutException), Because = "the broker is slow to warm up")]
[ClearsOnRecycle("rabbit,kafka", typeof(BrokerUnavailableException))]
[ClearsInFreshProcess(typeof(BadImageFormatException))]
[NeverRecovers(typeof(SomeDeterministicBug), Because = "this is a real bug")]
public class OrderSpecs;
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
    directly.** The console project did not — before it moved to Stoat — and picked up 2.36.1
    transitively from `WolverineFx.Http`
    — Central Package Management only pins what a project actually references, so the central
    2.37.0 did not apply and it compiled against a JasperFx nobody chose.
  - `ClearsOnRecycle` parses its resource list independently of `ResilienceTags.ParseResources`,
    because JasperFx must not reference Bobcat. `ResourceParsingAgreementTests` pins the two
    together; without it they can diverge silently and a trait and a hint naming the same brokers
    would resolve to different names.
- **`HintedFailurePolicy` sits between the user's policies and `DefaultFailurePolicy`** — explicit
  code still wins, and any failure no hint describes falls through to the trait-driven default
  unchanged. A run with no hints behaves exactly as it did before.
- **A hint never widens the budget.** `RetryBudget.MaxAttemptsPerTest` is the operator's ceiling;
  an unconfigured run still retries nothing however many hints are declared. Knowledge of what
  recovers belongs to the test's author, how much time the run may spend belongs to whoever runs
  it, and conflating them would let a spec author escape the ceiling.
- **`NeverRecovers` is the counterweight.** Without it the only way to stop a broad `Retry=3`
  re-running a deterministic bug is to remove the trait, which also stops the retries that were
  pulling their weight.
- **`FailureSignature` is the matching key** — type *names*, not `Type`, because out of process a
  name is all there ever is. In process it holds the whole inheritance chain, so a hint on a base
  class matches a derived failure; from the MTP wire it holds one name, so a base-class hint
  abstains. That asymmetry is deliberate and degrades to "no hint applied" — never to a wrong
  retry. A worker that erases the type entirely (tUnit) simply matches nothing.
- **Hints cross the process boundary through the spec manifest, not as attributes (issue #469).**
  The supervisor drives the worker as a process and never loads its assembly. The worker can read
  the attributes — it is the process they live in — so it writes them into its `SpecManifest` as
  `SpecManifestHint`s (the declaration's own coordinates: assembly, class or method), and the
  supervisor joins those to the uids its discovery reported and adds them to
  `Supervisor.RecoveryHints` (`ReadWorkerRecoveryHints`, on by default). Hints registered by hand
  still work and come first. This is why `RecoveryHint` exists separately from
  `RecoveryHintAttribute` — it is also the shape the ledger's proposals take.
- **Scope beats type specificity.** Assembly < class < method, and the narrowest declaration wins
  even when it names a less specific type, so a class can override a run-wide default without
  knowing what that default names. Within one scope, the hint naming the exact type wins.
- **The hint travels on the `Disposition`**, not just inside its `Reason` prose, because the case
  most needing a report is a hint that *suppressed* a retry — otherwise a test with a `Retry` trait
  that failed once looks like its trait stopped working. Surfaced as `↯ recovery hint applied` in
  the console and as a `hint` object per attempt in `RunReport.ToJson`.

**Layer 2, the committed ledger, is built** (`src/Bobcat/Ledger/TestLedger.cs`, design of record
`design/ledger-design.md`) — one store serving #44's hint proposals, #142's duration trends, and
`Supervisor.KnownTestDurations` (the third consumer that made it infrastructure:
`ledger.KnownDurations()` feeds `WorkPlan`'s balancer from the first pass). The open questions were
settled by making the merge strategy the design: a grow-only set of per-(test, run) observations
plus a deterministic clock-free prune — `Record`/`Merge` commutative, associative, idempotent;
serialization canonical (byte-identical for the same observations, whoever folds) — so a git
conflict always resolves by load-both-sides → `Merge` → save, no artifacts needed. Failure classes
are keyed by type *name* (the `FailureSignature` rule); aging is newest `MaxRunsPerTest` per test
plus explicit `PruneTestsNotSeenSince(cutoff)` for deleted tests. Fed by
`SupervisorLedger.From(SupervisorResults, runId, at)` (every attempt survives). Nothing writes the
ledger implicitly — recording is the caller's line of code, and stall-induced entries (#173) never
feed hint evidence. The DECIDED fork stands, now in code: `ProposeHints()` emits attribute *text*
with `Because` evidence (plus the `[NeverRecovers]` counterweight, `minOccurrences` gating both) — a
human accepts by writing it into the code, and nothing reads proposals back into an
`IFailurePolicy`; a policy that silently learns "just retry this" is exactly how red gets laundered
into green with nobody deciding to.

### Listing and running by specification identity (issue #391)

A monitor only ever names **identities** — `{Feature}/{Scenario}` — and never a test framework's
own filter. Three pure pieces in core plus one artifact:

- **`SpecIdentity`** names the string every consumer keys on (the retry budget's test id,
  `DeclaredSteps`'s key, `scenario_finished`'s uid, the ledger, the timing report, the generator's
  descriptor, `WorkPlan`'s key). Comparison is **ordinal and exact**: this is a machine identity,
  not a search, so folding case would let two scenarios differing only in case collide into one —
  silently running the wrong spec, which is worse than a miss, because a miss is reported. Nothing
  offers to *parse* an identity: a feature title may contain a `/`, so the pieces are read off the
  model, never out of the string.
- **`SpecSelection`** is the set a run was asked for. **Empty means everything**, so a run path
  takes one unconditionally; `NarrowsAnything` is how a caller tells "nothing in particular" from
  "asked, and it covers the suite" — the distinction MTP forces, since it ignores a subset
  parameter it does not understand and runs the whole suite (`GuardAgainstAnUnfilteredRun`'s
  lesson). `NotIn(known)` is how a runner rejects a foreign identity **by name, before running**,
  rather than running a narrowed suite that matched nothing and exiting 0.
- **`SpecManifest`** is what a suite says it specifies, written to the path `BOBCAT_LIST_SPECS`
  names and **only when something asked**. A file, not stdout, because the listing has to cross a
  process boundary — a resident runner is not the suite, and for a projected suite it *cannot* be.
  Not `--list-tests` either: that prints display names, not identities. Writing it **can never
  fail the suite** — same invariant as the monitor probe. `Framework` (xunit/tunit/nunit/mstest)
  settles the filter spelling; `Lane` is always `projected` now. It also carries the suite's
  recovery hints (#469, see Resilience).
- **`SpecFilterArguments.For(manifest, selection)`** is the translation: a projected suite's
  platform uid is its framework's own (assembly + class + method + arguments), so the identity
  becomes `--filter-method Ns.Class.method`. It **refuses rather than guesses** — an unknown
  framework, an unbound entry, or an identity the manifest lacks all throw. **NUnit and MSTest are
  refused**: they run on MTP 1.9 here, so verifying theirs is the follow-up. An unverified filter is
  exactly the run that looks filtered and is not.
  - **TUnit is `--treenode-filter "/*/<Namespace>/<Class>/<Method>"` (bobcat#470)**, measured
    against the real `Bobcat.TUnit.Tests` host (`TUnitTreeNodeFilterTests`): the namespace is one
    segment with its dots kept, a nested class is `Outer+Inner`, segments match whole words, a
    data-driven method's path selects every case, and a filter matching nothing exits 8. **The
    option is accepted once and ORs only within a segment** (`/*/Ns/(A)|(B)/(x)|(y)`): a whole-path
    alternative is a parse error, and an unparenthesised `|` between two paths silently selected
    the wrong tests. So several identities become one filter only when they are exactly the cross
    product of their namespaces × classes × methods (one class, or one method name across classes);
    anything else is **refused** rather than over-selected. A class in the global namespace is
    refused too (that spelling is unmeasured). Warm runs by uid and has neither limit.

**The manifest is written from the generated module initializer**, which calls
`DeclaredSteps.Bind(uid, class, method)` and then `SpecManifest.WriteIfRequested`, because that
initializer is the only code Bobcat owns in that process and the only moment sure to run —
discovery loads the assembly, so `--list-tests` reaches it without executing a test. The binding is
**recorded, not re-derived**: the identity is a one-way function of the names and an explicit
`[BobcatFeature("…")]` title shares nothing with its class name. `MarkerCommentSpecs.FrameworkOf`
type-probes for each adapter's `BobcatScenarioAttribute`, the same pattern as the
`EventModelSliceDescriptor` gate.

- **A binding is emitted on a wider rule than `DeclaredSteps.Register`**, and the difference is
  load-bearing. A projected spec declares its steps by marker comments, by `[BobcatStep]`
  interceptors, **or by grammar calls** — and only the first reaches `Register` (an unmarked test is
  deliberately not a declared-steps entry). Keyed on that set, the first cut of this listed **8 of
  `Bobcat.Xunit.Samples`' 41 specifications** while the other 33 rendered and published verdicts
  perfectly well. What makes a test a specification is that `[BobcatScenario]` records it, so
  `Bind` keys off `OpensRecording` and `HasAnything` replaced `HasSteps` as the gate on emitting the
  initializer at all.

- **Duplicate identities and the explicit scenario name (bobcat#489).** Two projected specs
  resolving to one identity is **BOBCAT036** (error), computed in
  `MarkerCommentSpecs.DuplicateIdentities` over `IdentityOf`, the same string `Emit` writes, so it
  cannot disagree with the runtime. `[BobcatSpec(Name = "…")]` states the scenario title verbatim:
  the generator reads it by name (`ScenarioTitleOf`, used by every identity site: marker specs, the
  step interceptors' `UidOf`, `SpecificationManifest`, the Event Model), the runtime through core's
  `IExplicitScenarioName` (`MarkerSpecNaming.ScenarioTitle(MethodInfo)` / `(Type, name)`). The
  run-time guard: `DeclaredSteps.Bind` keeps the FIRST binding and records the rest
  (`DeclaredSteps.Duplicates`); the manifest carries them as `Duplicates`, `WriteIfRequested`
  prints them on stderr and **still writes** (never fail the suite), and `SpecFilterArguments`
  refuses a duplicated identity by name.

`Bobcat.Xunit.Tests/SpecIdentity/ProjectedSpecIdentityEndToEndTests` pins it against the real
`Bobcat.Xunit.Samples` host — listed and then run by identity.

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
  than hosting the platform. That is part of what lets it drive any MTP worker, Bobcat or not —
  `ForeignFrameworkTests` drives `Bobcat.Tests` (a plain xUnit v3 host) to prove it, not just
  assert it.
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
  naming signal every front-end supplies. It handles `Feature/Scenario` and the dotted
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
  database every class truncates. That needs per-worker environments
  (`MtpWorkerFactory.EnvironmentFor`), which is still unbuilt. `PersistenceTests` did not need it;
  running several *projects* concurrently will.
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
  - **The test that was *running* when the worker died was the exception to that, until #445 tripped
    over it.** Nodes arrive at least twice — in-progress, then final — and `MtpWorkerClient`
    records the in-progress update, so such a test already HAS an outcome: `Indeterminate`, with a
    null `ErrorMessage`, because an in-progress node carries no `error.message`. `Complete`'s
    unreported-tests pass skipped it for having an entry, so the one test a reader most wants
    explained reached the report unexplained — and `SupervisorEndToEndTests` met it as a
    `NullReferenceException` inside a Shouldly predicate rather than as a failed assertion.
    `Complete` now stamps the fault onto any non-terminal outcome with nothing to say, and never
    over a verdict the worker did give. Found because #445 made the final node update bigger, which
    shifted a crash race that had always been able to land here.
- **`RetryAfterRecycle` recycles, then runs the test alone in a fresh process.** Resources are
  registered with `Supervisor.AddRecyclableResource` (an `IRecyclableResource`;
  `DockerComposeResource` is the shipped one). Reusing the
  shared worker would leave it connected to the broker we just discarded. Naming a resource
  nobody registered is reported as a wiring mistake, not silently retried without recycling. A
  recycle that *fails* aborts the run with `AbortReason` rather than throwing, so results
  gathered before the infrastructure broke survive.
- **`ISupervisorObserver` narrates the run live** (`Supervisor.AddObserver`, issue #84): every
  attempt with the verdict that followed it, scheduled retries, lane start/finish, recycles, worker
  faults. Default no-op members, and an observer that throws is logged and stepped over — a
  dashboard must not be able to fail a test run. A scheduled retry carries the **true attempt
  number**, which is the fact only the supervisor has: a worker counts from one on every run
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
  `Bobcat.Supervisor.ProjectedWorker`'s `Basics/hangs when armed` (`BOBCAT_HANG`) is the reusable
  wedge.
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
  Posted by `SupervisorRunPublisher`; how the receiving side folds and renders them is Stoat's,
  and kept in `docs/monitor-design.md` (Bobcat-side seams item 4) because the fold is half of
  the contract — a publisher that does not know how its events are read cannot tell an
  additive change from a breaking one.
- **Preflight runs once before any worker is launched** (`Supervisor.Preflight`, which also checks
  every recyclable resource). See the environment-check note below.
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
    `design/ledger-design.md`. Layer 2 (sleep-shaped-duration heuristics) stays **not built**:
    the cross-run variance it needs exists now, but its false-positive question is open and
    `[SlowByDesign]` belongs in `JasperFx.Testing` per the #63 precedent.

`Bobcat.Supervisor.ProjectedWorker`'s specifications can *detect* whether they were isolated (a
static per-process counter), so the isolation tests prove isolation happened rather than merely
proving a process was launched. There is a control test showing the same test fails when batched.
It is an ordinary projected xUnit v3 suite (serial, declaration-ordered, traits as plain
`[Trait]`s).

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

### The Critter Stack helpers are store-agnostic (`src/Bobcat/CritterStack/`)

Decision of record 2026-08-20 (issue #103): Bobcat's event-sourcing helpers bind to the
**`JasperFx.Events` abstractions**, never to Marten — the same discipline `Wolverine.CritterWatch`
lives by — so one package serves Marten, Polecat and Fisher. They live in **Bobcat core** (the
`Bobcat.CritterStack` namespace), and reach the store through the `JasperFx.Events` package; **core
does not reference Marten, Polecat or Fisher**, and a spec project using it needs none of those
either (`samples/BankAccountES/Tests` has no `using Marten`). The document side (`DocumentStores`)
goes through `JasperFx.Events.Documents`.

- **The store comes from the host's container, not from a Bobcat resource type.** Marten
  (`AddMarten`), Polecat (`AddPolecat`) and Fisher (`AddFisher`) all register their store as
  `JasperFx.Events.IEventStore`; `services.EventStore()` (`CritterStackHostExtensions`) resolves it
  from the host's services. `storeName` (matched on `IEventStore.Identity.Name`) disambiguates a
  host with several stores; with no `storeName`, the host's one **main** store is the default (see "Several event
  stores" below).
- **Everything the abstractions cover goes through them.** Streams are read via
  `IEventStore.OpenReadOnlyEventStore()`; projection waits via
  `IEventDatabase.WaitForNonStaleProjectionDataAsync` (the same wait JasperFx's
  `ProjectionScenario` performs) and `AllProjectionProgress`, so it does not matter who runs the
  daemon — Marten's `IProjectionCoordinator`, Fisher's hosted service, or Wolverine. A projection
  wait decides *what* to wait on from the store's configured shards (`IEventStore<,>.AllShards()`),
  not from the progress table: a daemon writes a shard's row only after its first batch, so right
  after the first append an empty table is indistinguishable from "no async projections" and a wait
  keyed on it passes vacuously. A name matching no configured shard throws and lists them.
- **One tenant's wait asks the store first** (`AfterAsyncDaemonsCatchUpAsync(tenantId)` →
  `EventStores.WaitForNonStaleProjectionsAsync(store, tenantId, …)`, wolverine#4962 phase 2): JasperFx 2.85.0's
  `IEventStore.FindDatabaseForTenantAsync` (a default member — the single database when certain, else null),
  then Marten's `Storage.FindOrCreateDatabase` by reflection for a Marten that predates the override, then every
  database. `EventStoresTests` pins all three with a fake store that re-implements the default member.
- **Two gaps in JasperFx.Events 2.53.0 are bridged by convention, and say so.** There is no
  abstraction for *aggregating* a stream or for *wiping* a store. `EventStores.AggregateStreamAsync`
  uses the read-only view when it is an `IQueryEventStore` (Marten, Polecat) and otherwise opens a
  session through the `IEventStore<TOps,TQuery>` closure and finds its `Events` member (Fisher).
  `EventStores.ResetAllDataAsync` follows `Advanced.ResetAllData(ct)` / `ResetAllDataAsync(ct)` /
  `Advanced.Clean.DeleteAll*` — the shape all three stores share — and a store matching none gets an
  exception naming what was looked for. JasperFx's own `IStatefulResource.ClearState` is *not*
  enough: Marten's database does not implement `IDatabaseWithRewindableState`, so `jasperfx
  resources clear` is a no-op for it (`ClearStatefulResourcesAsync` is still offered, for
  Wolverine's envelope storage). Both are a bounded, documented softening of "no reflection"; when
  the abstraction lands upstream, the convention path is what gets deleted.
- **`ProjectionScenario<,>` is not what the waits delegate to, and that is a decision, not a gap
  (2026-08-21, JasperFx.Events 2.53.0).** Checked on the bump: it is a *scripted* harness — it wipes
  data (`DeleteExistingData` defaults to true), builds and owns its **own** daemon, appends through
  its **own** session, and is reached only through each store's `Advanced`
  (`EventProjectionScenario` on Marten and Polecat, `EventProjectionScenarioAsync` on Fisher), each
  closed over that store's session pair with no non-generic interface and no `IEventStore` accessor.
  A Bobcat spec appends through the application's own handlers and waits on the host's daemon, so
  the right tool is what the scenario itself calls after each batch —
  `IEventDatabase.WaitForNonStaleProjectionDataAsync` / `IProjectionDaemon.WaitForNonStaleData` —
  which is what `EventStores` already does. Nothing in 2.53.0 retired a convention path either:
  there is still no aggregate-stream member on `IReadOnlyEventStore` (Fisher's view is not an
  `IQueryEventStore`, so `AggregateStreamAsync` still opens a session through the `IEventStore<,>`
  closure — proved on the real store) and still no reset abstraction (`Advanced.ResetAllDataAsync`
  is Fisher's spelling — also proved). The `IProjectionCoordinator` fallback stays for a store that
  does not override `IEventStore.AllDatabases()`; all three do.
- **Fisher is the inner-loop target, and is covered by the samples.** The current aligned version
  set is the header of `src/Directory.Packages.props`. `Bobcat.CritterStackScenario.Tests` covers
  the convention paths against fakes shaped like each store (`EventStoresTests`) and the scenario on
  real Marten (Postgres 5445, `[PostgresFact]`) and real Fisher (temp SQLite), with no Wolverine;
  the samples' Fisher leg is BankAccountES. The Fisher host needs
  `ApplyAllDatabaseChangesOnStartup()` registered *before* `AddAsyncDaemon` — Fisher builds its
  schema lazily and the daemon reads the progression table on start
  (`design/critter-stack-interop-notes.md`). Polecat needs SQL Server and is a documented manual
  run: `AddPolecat(...)` registers `IEventStore` like the others and its `ProjectionScenario` and
  reset share Marten's spellings, so the same code path applies.
- **Acceptance (#103): `samples/BankAccountES` runs on Marten and on Fisher — the same specs on each
  — with no store-specific Bobcat reference, switched by `EventStore=Marten|Fisher` in
  configuration.** The host was rewritten to the store-agnostic vocabulary (`[DeciderFunction]`,
  `[Entity]`, `Storage.StartStream`, `IEventStoreOperations`, `IDocumentReadOperations`, a
  self-aggregating `Snapshot<T>` read model in place of a `SingleStreamProjection<,>` subclass) so
  that `Program.cs` is the only file naming a store; the swap table is in
  `design/critter-stack-interop-notes.md`. The Fisher leg runs in CI (the `TestSamplesOnFisher`
  target, via `samples.yml`), because a SQLite file needs nothing the runner does not have — which
  is the whole argument for Fisher as the inner loop.

#### `CritterStackScenario` — the store-only vocabulary (bobcat#471)

Decided as **Option A** on #471: a code-first scenario in core
(`src/Bobcat/CritterStack/CritterStackScenario.cs`, with `CritterStackSpec` as the xUnit/TUnit base
class and `CritterStackActOutcome` as the act's capture) that needs **no Wolverine** — plenty of
applications use Marten, Polecat or Fisher without it. WolverineFx.Bobcat's `WolverineScenario`
derives from it and adds only the acts that go through the tracked session, the message assertions,
the DCB boundary steps and correlation.

- **Public signatures and step text are `WolverineScenario`'s, verbatim**, so deriving was "delete
  duplicates" with no API break: Givens (tagged, strong-typed-id, aggregate-less, read
  model, and since #502 `GivenContext` — `GivenEvents`'s append under `SliceRole.Context`, which never
  takes over the scenario's own stream `_stream` from a `GivenEvents`), the event assertions over the last act, the refusals, aggregates/documents/read models,
  DCB models, `Verify`/`ThenMatches`, `ResetAsync`, `storeName`.
- **The seams a derived scenario overrides:** `runActAsync(Func<IServiceProvider, Task>)` (the
  store-only act: high-water mark, run in a new scope, read back every event since, attributed by
  stream (#503, `attributeToSpecification`) — so `NewEvents == AllNewEvents`, `ForeignEvents` the
  rest, `StartedStreams` from version-1 events, `MessagesSent` empty, `EventTagsKnown = false`), `failureOf(message)` (the exception a wrong is thrown as outside a
  recording — `SpecAssertionException` here, Wolverine's `SpecificationFailedException` there), the
  protected `LastAct` setter, `ResetAsync` (virtual), and the protected helpers its own steps reuse
  (`actSucceeded`, `failureSuffix`, `verifySet`, `flatten`, `describe`, `recordValues`, `anyOf`,
  `tagsOf`, `isOn`, `unwrap`, `describeTypes`, `exceptionsIn`, `hasStore`, `recordTouched`,
  `tagsCanBeJudged`, `fail`/`fact`, and #503's `claimStream`, `attributeToSpecification`,
  `crossTalkSuffix`).
- **Store-only attribution (#503).** The scenario's own streams are every stream a Given arranged
  (`GivenContext` included), `WhenActingOn` addressed, or an act wrote to or started; an event on any
  other pre-existing stream is `ForeignEvents`. Whole-store steps (`ThenEvents*`, `ThenEmitted`,
  `ThenNotEmitted`, `ThenNoEvents`, `TheEvent`, `ThenCommandRefused`, the started-stream steps) judge
  only the own events and, **when they fail** with `ForeignEvents` non-empty, append "cross-talk
  suspected: N event(s) …"; a passing one stays quiet. Stream- and tag-addressed steps
  (`ThenEventsOn`) look at own + foreign (`addressable()`), since naming the stream already scopes them.
  Residual, documented: a stream another test *starts* during the act still counts, so whole-store
  assertions on a shared store still need serial execution (BankAccountES keeps
  `Parallelization(Mode = ParallelMode.None)`). DCB: no store reads tags back, so a tagged append is attributed by its
  stream; where tags are returned, a tag value the spec arranged events with also counts.
- **No store reads an event's tags back** (checked on Marten and Fisher), so after a store-only act
  a step that judges tags fails saying so (`tagsCanBeJudged`) instead of reporting every tagged
  event MISSING. Name the stream, or read the boundary's model with `TheDcbModel<T>`.
- **Two refusal styles, two steps (issue #168).** `ThenValidationFails(reason)` /
  `ThenRefusedWith(reason)` describe a refusal that *throws*. A handler that refuses *without*
  throwing (a messaging railway's `Stop`) is `ThenCommandRefused()`: the act ran, nothing threw,
  nothing was appended. Deliberately **reason-less** — a clean stop carries no reason anywhere an
  act surfaces, so a reason clause would assert on nothing.
- **Also on the scenario:** the stream-version steps (`TheStreamVersion`, `ThenEventsOn<T>(id,
  afterVersion, …)`, `ThenNoEventsOn<T>(id, afterVersion)`, `WhenActingOn<T>` as one `Paragraph`); a
  configurable `ProjectionTimeout` (**20 s**); refusal-reason matching across inner and aggregate
  exceptions, compared in **one** place (`ReasonComparison`, Ordinal until exact-vs-ignore-case is
  decided); `ThenMessagesSent<T>(count)` over `CritterStackActOutcome.MessagesSent` (always empty
  for a store-only act, filled by a bus); and the clock steps, recorded over `BobcatClock`
  (`GivenTheDateIs`, `GivenTheTimeIs`, `WhenTheClockAdvancesBy`, `WhenTimePasses`,
  `WhenTheClockAdvancesTo` — synchronous on purpose, because the clock is `AsyncLocal` and a value
  set inside an async method never flows back).
- **Catch-up timeouts before a read are errors that say why (#497, decided C).** `ThenDocument`,
  `ThenReadModel`, `ThenNoReadModel`, `ThenSingleReadModel`/`ThenSingleDocument` and
  `ThenProjectionsAreCaughtUp` wait through `waitBeforeReading`, which rethrows a store's
  `TimeoutException` as `ProjectionCatchUpTimeoutException` (still a `TimeoutException`, so the step is
  `error`, never `failed`): the wait, `EventStores.ProjectionsBehindAsync`'s best-effort
  `'shard' at N of M` list, the timeout, the type and identity read, the original as `InnerException`.
  The #497 root cause: Marten's `WaitForNonStaleProjectionDataAsync` throws an **immediate**
  `TimeoutException` ("No event activity was detected") on a store with no events, because a fresh
  `mt_events_sequence` reports last value 1 with zero events. So `ThenDocument` on a fresh schema threw
  when the test ran first in its class (xUnit's order follows the assembly path) and passed after any
  test had appended. `WaitForNonStaleProjectionsAsync` now returns at once for a single-database store
  whose read-only view holds no events; `reading_an_empty_store_on_marten` pins it in a class of its own.
- **Run evidence (#465):** `ScenarioRecorder.RecordTouchedType(Type)` is an ambient accumulator on
  the recording, static and `AsyncLocal`-backed for the reason `SpecReport` is (#408);
  `Recording.TouchedTypes` reaches `scenario_finished.TouchedTypes` through `MonitorTouchedTypes`,
  so Stoat's MCP/API field keeps its shape. Observed, never asserted: arranged aggregates and
  events, the events an act appended, the models and documents loaded — never a type a Then step
  only expected.
- **Tested in `Bobcat.CritterStackScenario.Tests`**: every member on Marten (Postgres 5445, a schema
  per host) and on Fisher (temp SQLite), with no Wolverine anywhere in the project.

#### Several event stores in one specification (bobcat#504, decided A + B + C)

- **A, route by type.** On a host with several `IEventStore`s every Given and Then goes through the protected
  `storeForAsync(step, model, values, tagTypes)`: the one store whose registrations claim the model type (an
  aggregate, projected or document type), else its events' or tags' types, else the default store (the
  constructor's `storeName`) when it is among the claimants or nobody claims it, else a `SpecCriticalException`
  listing the candidates and naming `In<TStore>()` / `In("name")`. Ownership is read store-agnostically by
  `StoreCatalog` (`StoreRouting.cs`) from `IEventStore.TryCreateUsage` (subscriptions' `AggregateType` and
  `AppliedEvents`, `Events`, `RegisteredEventTypes`, `TagTypes` and their aggregates) and
  `IDocumentStoreUsageSource.TryCreateUsage` (`Documents`); a store that returns null or throws owns nothing.
  Catalogs and decisions are cached per specification (`StoreRouter`). Stores register some types lazily (an
  event type on first append, a document mapping on first store or load), so ownership can grow during a run.
  **A single-store host never reaches the router**: `storeForAsync` returns `Store` before anything is read.
  A raw stream addressed by id alone (`TheStreamVersion(id)`) has no type and goes to the default store.
- **B, explicit addressing.** `In<TStore>()` resolves the marker from DI. If that is not itself an `IEventStore`
  (Fisher registers a `DispatchProxy` under the marker and the unwrapped store as `IEventStore`), it falls back to
  the store whose `Identity.Name` equals the marker's type name. That is the default name all three stores give
  an ancillary store: Marten and Polecat set `StoreName = typeof(T).Name`, and Marten and Fisher lower-case it in
  the identity. `In(name)` matches `Identity.Name`, ignoring case. Both return a **view**: a `CritterStackScenario`
  built by the protected `CritterStackScenario(root, store)` constructor, pinned to that store and sharing the
  root's private `SpecificationState` (own streams, arranged tags, last act, `ProjectionTimeout`,
  `UnspecifiedValues`, the router). A view's `runActAsync` and `failureOf` delegate to the root's, so an act
  through a view is not split and is not recorded twice. `createView(store)` is virtual, so a derived scenario can
  hand out its own view type. `CritterStackSpec.In` forwards.
- **C, every store observed.** `runActAsync` is `highWaterMarksAsync()` (one `StoreHighWaterMark` per store
  from `storesToObserve()`), then the act, then `readBackSinceAsync(marks, error)`. That reads each store since
  its mark, applies #503 attribution per store (one shared own-stream set, keyed by stream id), lists events
  store by store, and fills `CritterStackActOutcome.StoreByEvent` / `StoreOf(e)` only when more than one store
  was observed. Whole-store failures append `storesSuffix()` ("Appended by store -- 'main': …"). Stream- and
  tag-addressed steps order the addressable events store by store, then by sequence.
  `AfterAsyncDaemonsCatchUpAsync` waits on the default store plus every store the specification **touched**
  (`SpecificationState.Touched`: a routed step's store, a view's store from `createView`, a store an act appended
  *own* events to in `readBackSinceAsync`; cleared by `ResetAsync`), or on every store when there is no default
  (a view, on its own). `waitBeforeReading` waits on the store being read. Read-back still observes every store.
- **The main store is the default (CritterWatch embedded).** `AddCritterWatchEmbedded` registers the
  console's own ancillary Marten store (`AddMartenStore<ICritterWatchStore>`, identity `icritterwatchstore`), so a
  spec host had two stores and every unqualified step was refused. Now, with no `storeName`,
  `CritterStackHostExtensions.EventStore()` / `DefaultEventStore` take the only store, else the **one main
  store**, else throw (message lists each store as `[main]`/`[ancillary]` and names `storeName`, `In<TStore>()`,
  `In("name")`). The router's default and `Store` use the same rule. **The signal is the registration, not the
  name:** `IsMainEventStore` finds the non-generic interface named `IDocumentStore` on the store's own type
  (Marten's, Polecat's, Fisher's) and checks that the container resolves it to that very instance. All three
  register their main store under `IDocumentStore` and an ancillary store under its marker only, so for an
  ancillary store that service is another instance or nothing (Fisher's unwrapped ancillary store too; Polecat
  does not put ancillary stores in `GetServices<IEventStore>()` at all). Rejected: `Identity.Name == "main"`
  (only the default `StoreName`, user-settable), `Identity.Type` (the product, not the role), the
  non-generic `IProjectionCoordinator` (exists only with a daemon). A store with no `IDocumentStore`
  interface (fakes, in-memory) is never main, so such hosts keep the refusal. Waits ignore an untouched
  ancillary store (above), so CritterWatch's store cannot stall a spec; its projections are all inline today
  anyway, so Marten's wait returns at once for it.
- **Not decided, so not built:** whether the Event Model records which store a slice's aggregate lives in
  (a JasperFx descriptor change); and an event type registered in more than one store, which is addressed
  with B only (routing by events then fails with the candidate list).
- **Tested:** `store_routing` (fakes, no database: the routing rules, caching, the single-store no-read
  guarantee, views, marker fallback), `default_main_store` (fakes registered under a fake product's
  `IDocumentStore` or a marker: main beside ancillary is the default, two mains or ancillary-only refuse, one store
  is the default whatever its role, waits skip an untouched ancillary store), `ancillary_stores_on_marten` /
  `ancillary_stores_on_fisher` (a real main + ancillary store on each, unqualified steps going to the main store),
  and `main_store_default_on_marten` (an ancillary store whose async projection never runs: an unqualified spec
  passes and its wait never touches that store, while `In<IConsoleStore>().AfterAsyncDaemonsCatchUpAsync()` times
  out). Polecat has the same `AddPolecatStore<T>` / `IDocumentStoreUsageSource`
  surface but needs SQL Server, so it is a manual run, as elsewhere.
- **WolverineFx.Bobcat follow-ups (wolverine#4977):** `WolverineScenario.runActAsync`'s high-water fallback
  still reads one store and should use `highWaterMarksAsync` / `readBackSinceAsync`. Its tracked-session
  outcome cannot fill `StoreByEvent`. `ThenEventuallyCaused` and its other own steps still read `Store`. Views
  are plain `CritterStackScenario`s, so its message and boundary steps are not on them until it overrides
  `createView`. `WolverineSpec` needs `In`.

## Package Structure

Every shipped package is `<IsPackable>true</IsPackable>` in its own csproj (src/ defaults to false)
and is packed by the Nuke `Pack` target; `./build.sh Pack` lists exactly these nine.

| Package | Target | Responsibility |
|---------|--------|----------------|
| **Bobcat** | net9.0; net10.0 | Runtime: the projected specification recorder, verification (`TableRunner`, `PropertyCells`, `SetVerificationComparer`, `ObjectSetVerification`, partial objects), rendering, the monitor wire, the resident runner, and the store-agnostic Critter Stack vocabulary (`CritterStackScenario`/`CritterStackSpec`, `Bobcat.CritterStack` namespace, bound to `JasperFx.Events` only) |
| **Bobcat.Generators** | netstandard2.0 | Source generator: step interceptors, marker-comment specs, declared values, specification bindings, the Event Model descriptor, BOBCAT032 table checks |
| **Bobcat.Supervisor** | net9.0; net10.0 | Drives MTP hosts as worker processes; retry/isolation policy, parallel lanes |
| **Bobcat.Xunit** | net9.0; net10.0 | Projects xUnit v3 `[Fact]`/`[Theory]` tests into the Bobcat model (marker steps) |
| **Bobcat.TUnit** | net9.0; net10.0 | The same projection for TUnit `[Test]` methods |
| **Bobcat.NUnit** | net9.0; net10.0 | The same projection for NUnit `[Test]`/`[TestCase]` methods (an `ITestAction`) |
| **Bobcat.MSTest** | net9.0; net10.0 | The same projection for MSTest `[TestMethod]`s — references no MSTest; ships global hooks as source |
| **Bobcat.EventModel** | net9.0; net10.0 | Imports an eventmodelers.ai emlang board and writes it as C# stub types plus an `EventModelDefinition`; the curated YAML format is retired (#406) |
| **Bobcat.Console** | net10.0 | The `bobcat` global tool: `run` (the default), `preview`, `pick` and `watch` over a spec project, `import-event-model`, and `resident` for the out-of-process resident runner |

`design/history/acceptance-coverage-map.md` maps the retired acceptance suite onto
`Bobcat.Projected.Acceptance.Tests`; `Bobcat.CritterStackScenario.Tests` and
`Bobcat.Supervisor.Tests` cover the rest.

**Retired as a package (#407):** `Bobcat.Wolverine`. Its act, warm-up, transport draining and
`WolverineScenario` ship in **`WolverineFx.Bobcat`** from the Wolverine repo, so no Bobcat package
depends on Wolverine.

Not packed: `Bobcat.Xunit.Samples` and `Bobcat.Supervisor.ProjectedWorker` are hosts the tests drive.

**The console that receives all of this is not in this repository** (commit 3ee3db9, "Carve the
console out of Bobcat", 2026-09-18). The run board, the archive, the exports, the MCP tools over
them and the Event Model canvas — everything here that remembered something across a process —
moved to [Stoat](https://github.com/JasperFx/stoat). Bobcat has no web server, no store, no MCP
surface and no frontend: no `Bobcat.Console` project, no Vue SPA, no generated TypeScript
contracts, no `console-frontend.yml` gate, no `Bobcat.Console.Specs`. Stoat's
`ContractRoundTripTests` / `TypeScriptContractTests` keep *its* copy of the contracts honest, so a
change made here alone fails there; `Bobcat.Tests/Monitoring/FakeMonitorHost` is how this side is
tested against real HTTP without one. Decisions of record: `docs/monitor-design.md`, which keeps
its filename because it documents the wire at least as much as it ever documented the viewer.

What stays here is **the format and the run**, and none of its vocabulary is obsolete. Here
"monitor" means *the thing a run publishes to*, so renaming any of the following is a breaking
change for consumers that have nothing to do with the split — it needs its own decision, not a
sweep:

- The `Bobcat.Monitoring` namespace (`src/Bobcat/Monitoring/`), `Supervisor.PublishToMonitor`,
  `MonitorPublisher`, and the publisher's copy of the wire records in
  `src/Bobcat/Monitoring/MonitorEvents.cs`. **Two copies of those records is a decision, not drift**
  (issue #65) — the receiver's lives in Stoat, and the wire shape, not an assembly, is the contract.
  That is what let a BSL console absorb an MIT viewer without either side acquiring a reference to
  the other. The cost is that an additive field is **invisible** to the other side: `StepCell.Value`
  (issue #396) is read by nobody until a console is changed to read it, and nothing warns either
  party.
- `BOBCAT_MONITOR`, `BOBCAT_MONITOR_URL`, `BOBCAT_RUN_ID`, `BOBCAT_RUN_TAG`, `BOBCAT_RUN_OWNER`,
  `BOBCAT_RUN_COMMAND`, **`BOBCAT_RUN_COMMAND_FILE`** (issue #402 — a file whose contents are the
  CURRENT request's command id, which **wins** over `BOBCAT_RUN_COMMAND`; see the resident runner's
  warm suite for why a variable cannot serve), `BOBCAT_LIST_SPECS` (issue #391 — the path a suite
  writes its spec manifest to, and only when asked), `BOBCAT_RESIDENT` (issue #390 — the same
  request as `--resident`), `BOBCAT_RUNNER_ID` (issue #397 — the stable runner id a parent hands
  over, so the same checkout is the same runner across every relaunch), and the reserved `Monitor:*`
  configuration keys. **`BOBCAT_RUN_COMMAND` (issue #392) is `RunStarted.Command`** — the resident
  runner's command id, so a viewer can follow its own button press to the run it produced. Opaque
  like the tag, and deliberately independent of it: the tag says what work a run speaks for, the
  command says which request produced it, and a commanded run routinely carries both. It *is* a
  `BOBCAT_*` variable, unlike the session below, because here Bobcat is the thing asking — a
  commanded run is a child test host and the id travels down to it
  exactly as `BOBCAT_RUN_ID` does. **`CLAUDE_CODE_SESSION_ID` is read as well** (issue #389) —
  `RunStarted.Session`, opaque exactly like `Tag`, so a viewer can attach a run to the agent that
  ran it rather than inferring it from a working tree. Deliberately not a `BOBCAT_*` variable:
  Bobcat reads what an agent session already put in the environment instead of asking for it, so
  attribution needs no configuration. **It loses to the command (issue #401): a run carrying a
  `Command` reports no `Session` at all**, because a resident runner holds its launching agent's
  session for life and would otherwise claim every button press a person made. Every one is
  user-facing or on the wire. The `Monitor:*` keys that were ever *read* (`DataPath`,
  `RetentionDays`, `RetentionRuns`, `IdleMinutes`) belonged to the console and went with it, along
  with their `BOBCAT_MONITOR_*` spellings — nothing here reads one today. The prefix stays reserved
  rather than reused.
- **Port 5525**, the *default* address a publisher probes (`MonitorPublisher.DefaultUrl`).
  Deliberately kept rather than collapsed into Stoat's coordination port, because every publisher
  already probes it.

**`BOBCAT_MONITOR_URL` is a LIST, and a run publishes to every target (bobcat#530).** Comma- or
semicolon-separated; `MonitorPublisher.ParseUrls` trims, drops empties and drops duplicates (keyed
on the `Uri` each entry resolves to, so a trailing slash is not a second target — a doubled target
would double every event). Two consumers now want the same events and only one can hold 5525:
`bobcat ui` (#524's command) keeps it and Stoat listens on its own, so it can show a gate's live progress
(`run_progress`, the lane events, `test_stalled`, `worker_faulted`, step progress) while a long
suite runs. **Forwarding from `bobcat ui` to Stoat was the alternative and was decided against** —
it would make Stoat's live view depend on `bobcat ui` running.

- **`MonitorTargets` is the fan-out, and the isolation is structural rather than promised.** It
  holds one `MonitorPublisher` per URL, and a publisher already owns its client, its bounded
  channel, its drop-on-backpressure policy and its give-up counter — so *"a run is never slowed or
  failed by the monitor"* holds **per target** with nothing added: an unreachable Stoat cannot cost
  `bobcat ui` one event, and the reverse. That is why the list is a composite over the existing
  publisher rather than N targets inside it; the thing the invariant is a property of is one target.
- **Probes run concurrently**, so N absent consoles cost one probe timeout rather than N. Nobody
  answering is the same "no publisher at all" as before (null, and the run proceeds), and
  `BOBCAT_MONITOR=0` still suppresses every probe.
- **The Event Model `PUT` goes to every target, each asked its own model-name question** — `GET
  /api/event-model` merges only the sources naming the name *that* console serves, so the decision
  is per console: one serving another model gets the half withheld with a notice **naming it**,
  while the others still receive it. `SpecEventModelPublisher.PublishAll(MonitorTargets, …)`
  publishes to the targets **concurrently** so the whole exchange stays inside one `Ceiling`, and
  each target's notices are collected and replayed on the calling thread — `notify` stays a
  single-threaded callback.
- **Taking commands stays singular.** `MonitorPublisher.ResolveUrl()` (first entry) survives for
  the one caller that needs one origin: `ResidentRunner`'s command stream. `bobcat resident --url`
  accepts the list, registers with the first entry, and passes the whole string down as
  `BOBCAT_MONITOR_URL` — commands come from somewhere in particular, a run may be watched from
  anywhere. Supervisor workers inherit the variable unchanged, so their per-scenario streams fan
  out exactly as the bracket does.

**The `bobcat` tool is `src/Bobcat.Console/`**: `run` (the default — a bare `bobcat` runs the spec
project found from the current directory), `preview`, `pick` and `watch` over a spec project,
`import-event-model` and `resident` (`docs/bobcat-tool.md`). Registration is explicit, never a
scan (issue #369). It carries the free, no-server half of the toolset — convert an eventmodelers.ai
board export into C#, optionally pushing the assembled model at a console's
`PUT /api/event-model`; and (issue #399) keep a suite whose process Bobcat does not own available
to a monitor, which has to ship from here because the console references nothing in this repository
and so cannot be handed a class to host.

- **`import-event-model` writes C#, not YAML (issue #405).** `CSharpModelWriter` emits two files:
  `<Model>Stubs.cs`, one **field-less** record per command, event, aggregate, message and read
  model the board named, and `<Model>.cs`, one `EventModelDefinition` declaring the slices through
  JasperFx.Events 2.81's fluent API. `EmlangReader` and `EmlangImport` are untouched — only the
  writer at the end of the pipe changed — and the command's **curated arm is gone**: a curated
  file is refused with a sentence pointing at `EventModelDefinition`, which is more use than the
  "unrecognized" it would otherwise fall through to.

- **`--specs` also writes the spec project when the spec directory has none (`SpecProjectWriter`,
  2026-10-12).** The `.csproj` (xunit.v3 4.x on MTP 2, `Bobcat`/`Bobcat.Xunit`/`Bobcat.Generators`
  pinned to the tool's own `ToolVersion` via `$(BobcatVersion)`, `WolverineFx.Bobcat` via
  `$(WolverineFxBobcatVersion)`, Alba, the IDE bridge, the `Bobcat.Generated` interceptor opt-in, a
  `ProjectReference` to the app), `AssemblyAttributes.cs` (`EventModelName`, serial
  `Parallelization`, `WebApplicationFactoryContentRoot`) and `global.json`. Rules that matter:
  - **Never overwritten, not even with `--force`.** An existing `.csproj` means the project is the
    user's: nothing is written and it is *checked* instead (missing references, no
    `EventModelName`), each gap a callout.
  - **The app is checked, never edited**: no public `Program`, no `RunJasperFxCommands`, no
    `WolverineFx.RuntimeCompilation` (every spec then dies on `TypeLoadMode.Dynamic`). Each is an
    `app-*` callout whose `Fix` is the exact line or `dotnet add` command. Project-level callouts
    have an empty `Slice` and carry `File`/`Fix`, which `--json` writes as `file`/`fix`.
  - **global.json: an existing one anywhere above is checked, never shadowed**; else it goes at the
    solution root (nearest `.sln`/`.slnx`, else git root), else beside the spec project. The SDK
    reads the one nearest the directory `dotnet test` runs in.
  - **No `--` inside an XML comment** in the generated csproj (MSB4025). The first cut wrote
    "`--specs`" in its header comment and no project would load; the tests `XDocument.Parse` it.
  - **`DefaultWolverineFxBobcatVersion` is the newest *published* WolverineFx.Bobcat**
    (6.49.0-alpha.3, built against Bobcat 0.38). Move it when a WolverineFx.Bobcat paired with this
    Bobcat ships; until then pass `--wolverine-version`.
  - `SpecProjectWriterTests.the_generated_spec_project_builds` imports a board and `dotnet build`s the
    result; it skips unless `BOBCAT_IMPORT_BUILD_WOLVERINE` (and `BOBCAT_IMPORT_BUILD_FEED` for a
    local feed) is set, because it needs NuGet and a WolverineFx.Bobcat that pairs with this Bobcat.

- **A re-import without `--force` is additive (`ImportMerge` + `ImportFileWriter`, 2026-10-12).** The
  command used to keep every existing file whole, so a new slice got stub and spec files but no
  declaration (and `wolverine scaffold` never saw it), and a new type on an old slice was silently
  dropped. Now an existing file is merged into: missing slice declarations at the end of the chapter
  definition's `Configure` (written via `CSharpModelWriter.SliceDeclaration` against the file's own
  `InChapter`/`InDomain`/`ForAggregate`, not a fresh import's), missing stub types at the end of the
  namespace, missing specs at the end of the `[BobcatFeature]` class, missing `global using`s.
  Rules that matter:
  - **Never change or remove anything that exists.** A type whose board members it lacks or types
    differently is `type-differs-from-board`. Members the code ADDED are not a difference.
  - **A new role on an existing slice is a reopening statement, never an edit (import-role-additions,
    0.60.3).** For each `Emits`/`Publishes`/`On`(`From`)/`Reads`/`Produces`/`Projects` the board gives a
    declared slice and NO statement in the project says, one `model.Slice("X").Emits<E>()…;` is appended
    to the end of the chapter definition's `Configure` (several roles = one statement, a call per line):
    `merged-slice-roles`, with `ImportCallout.Roles`. This works because JasperFx's
    `EventModelBuilder.Slice(name)` always adds a NEW slice builder, and `EventModelDescriptor.Merge`
    (via `GroupByName`, which every host path uses) folds same-name slices, same-rung lists unioned, in
    one definition or across definitions. Verified 2026-10-12 against JasperFx.Events 2.85.0 and
    origin/main 2.85.1; nothing new was needed in JasperFx. Rules that matter:
    - **The reopen repeats an explicit `.InChapter`/`.InDomain` of the declaration** when the default in
      force at the end of `Configure` is another. Without it the reopen takes the definition's chapter
      and the merge records a `SourceDisagreement` "contradicts itself" hotspot — found by probe, pinned by
      `a_slice_whose_declaration_names_its_own_chapter_is_reopened_in_that_chapter` (mutation-checked).
    - **Roles are counted project-wide (`SourceIndex.RolesOf`)**, so a role in the declaration, an
      earlier reopen, or a reopen moved to another file is never added twice. Opener-implied roles count
      (`View<T>()` produces T, `Query<T>()` reads T) and are never reported as removed.
    - **A slice declared outside its chapter's definition is not reopened** (another definition might
      name another model): `slice-differs-from-board`, whose `Fix` is the calls to add and where.
    - **Report-only, declared to declared** (the board's `SliceDeclaration` text vs the file's statements,
      `ImportMerge.FactsOf`): `slice-aggregate-differs`, `slice-trigger-differs`, `slice-pattern-differs`,
      `slice-role-removed-on-board`, `slice-removed-on-board`, each with a `Fix`. Only what the BOARD
      claims is compared: no board aggregate (`missing-aggregate` asked for one), a declared DCB decider,
      or a trigger added where the board states none are not differences — those are the corrections
      the import asks for, and reporting them would nag on every re-import. An inferred board aggregate
      carries an `Override` (`--aggregate`/`--starts-stream` naming the CODE's aggregate) that silences it.
    - **Removal can't be told from a hand addition**, so the messages say "leave it if you added it". A
      removed slice is checked in the definitions the import writes to and in every definition in
      `--out` with the import header (a chapter the board dropped); with `--chapter` the command passes
      the WHOLE board's slice names so a slice moved to a chapter left out isn't "removed".
  - **Roslyn locates, text inserts.** Parse the existing file (`LanguageVersion.Preview`; any syntax
    error is `merge-skipped-unparseable`), find the offset, insert re-indented text. Never re-print a
    tree: the "byte-identical except the insertion" guarantee is what the tests assert.
  - **The whole output directory is indexed (`SourceIndex`), not just the target file**, so a type,
    slice or spec class the user moved is never declared twice, and a new stub file leaves out a type
    another file declares (the layout can move a type between files when the board grows).
  - **A slice opener is a verb called on the builder itself** (`IsOpener`): `model.Command("X").Command<Y>()`
    declares slice X; matching any `Command` call took Y.
  - `--force` overwrites, as before; the spec project files are never touched; `TestSupport.cs` only
    written when missing. Idempotent: `importing_twice_changes_nothing_the_second_time`.
  - Bobcat.EventModel references `Microsoft.CodeAnalysis.CSharp` for this.

- **The curated format is retired as an authoring surface (issue #406, 2026-10-06), and what
  survives says what it is.** `CuratedModelWriter`, `FileEventModelSource` and `AddEventModelFile`
  are **gone**: there is no way to write a curated file and no way to register one as a
  Declared-rung source, which is what "YAML is not an authoring syntax" means in code. What was
  `CuratedModelFile` / `CuratedSliceShape` / `CuratedModelMapper` is renamed
  **`ImportedEventModel` / `ImportedSliceShape` / `ImportedModelMapper`**, because that is now what
  they are — the emlang importer's in-memory model, which `CSharpModelWriter` writes from and
  `ImportedModelMapper` projects to a descriptor for the `--url` push.
  - **`CuratedModelReader` is internal, not deleted, and the distinction is deliberate.** Retiring
    an authoring format means nobody can author or register one; it does not require destroying the
    parser our own fixtures are written in. `ImportedModelMapperTests` is ten fixture-driven tests
    of model-to-descriptor mapping, and rewriting them as hand-built object graphs is how coverage
    quietly changes during a retirement. It goes when those fixtures do.
  - **`EventModelFileSniffer` stays**, for one reason: it is how the import command recognises an
    old curated file and refuses it *with a sentence pointing at `EventModelDefinition`*, rather
    than reporting the "unrecognized" above. A helpful refusal is worth more than one fewer type.
  - **Two tests lost their subject rather than their claim.** A byte-for-byte YAML round-trip and
    an import-through-the-reader round-trip were both about the write/read hop; the first's
    substance was already pinned by `consumed_events_and_reads_from_are_declared_roles_of_their_own`
    (so it was deleted rather than weakened into a duplicate), and the second became
    `the_import_maps_clean_to_a_descriptor`, dropping only the hop. The C# path that replaced the
    YAML one is pinned by `CSharpModelWriterTests`, which compiles and runs its output.
  - **`*.spec-ownership.yaml` is retired with it**, and with it **BOBCAT025/BOBCAT026**. That
    removed a capability: declaring *forward*, before any code exists, how a slice will be
    specified. **The intent is that this moves onto attributes, and no such attribute exists yet.**
    `[BobcatSlice]` — binding a test that already exists to a slice — is untouched, and
    `SpecIdentityAudit` (#338) is the only gate of its kind.
  - **Field-less is the honest output**, not a shortcut: an emlang export carries no field
    information at all (the board's props are intentionally omitted), so a name is the whole truth
    it can tell, and inventing an `Id` is a guess every consumer then has to un-guess.
  - **`--strong-typed-ids` gives each aggregate an identity type (bobcat#512/#433), and the decision
    lives in one place — `StrongTypedIds`.** Off by default: an undeclared identity is a `Guid`,
    which is what every store defaults a stream id to, and typing one is a modelling choice no board
    expresses. The name is `{Aggregate}Id` because **Marten treats a type as a strong-typed identity
    only when its name ends in `Id`** (case sensitive), so the convention IS the requirement; it
    wraps what `StreamIdentityType` says the stream's id would otherwise be. The struct is written by
    Bobcat rather than left to `wolverine scaffold`, because an import whose stubs name a type
    nothing declares does not build — and it carries `ToString`, `IParsable<T>` and a nested JSON
    converter, each needed by something an imported model routinely does (an HTTP slice posts the
    command and reads the id back).
    - **One identity per STREAM, not per type.** An aggregate's `Id`, the `Id` of a command that
      decides against it, each `{Aggregate}Id` of a command against several, the identity member of
      its events, and the `Id` of a read model projected from it are one identity
      (`IdentityOwnerOf`). The read-model arm is correctness: a single-stream projection's document
      id must BE the stream's identity type.
    - **A member is an identity by name OR by the model's mark.** `{Aggregate}Id` is the name rule;
      a stream keyed by an email has an identity member called `Email`, which only `idAttribute`
      says. Retyping happens only where the field already holds what the id type wraps, so a
      declared `string` is never silently re-read as a `Guid`.
    - **The spec writer gets the SAME instance**, and that is the whole reason `Write` takes a
      `StrongTypedIds` rather than a bool: a `Guid` local handed to an `OrderId` member is an import
      whose two halves do not compile together, which is worse than no option at all. A typed
      identity renders as `new OrderId(Guid.CreateVersion7())` and has **no table-cell form** —
      `CellValues` builds primitives and cannot construct one — so `Cell` answers null and the value
      is written as code.
    - **An aggregate whose `{Aggregate}Id` the board already uses keeps a primitive id**, with a
      `strong-typed-id-name-taken` callout. Two types of one name in one namespace is output that
      does not build, which is the one thing a scaffolder must never produce.
  - **Everything goes through `ISourceWriter`**, the rule the issue states and the reason the bump
    had to come first (jasperfx#956's whitespace fixes). Backticks are the writer's stand-in for
    double quotes, so a literal backtick in a board label is swapped for a single quote rather than
    silently becoming one.
  - **Two mistakes the first cut made, both now pinned.** The definition class took the model name,
    which is also the namespace's, so `namespace K9Crush { class K9Crush }` compiled and then
    resolved every sibling stub reference against the CLASS first — `K9Crush.SwipeOnDog` stopped
    meaning what it says. And a generic verb (`model.Command<SwipeOnDog>()`) names the slice after
    the type, so using one where the board called the slice something else would have **split one
    slice into two silently**, the slice name being the merge key; where they differ the slice now
    opens by name and states its pattern and role separately.
  - **A role whose type has no stub is declared by NAME** — `.HandledBy("SwipeEndpoint")`. A
    handler is never stubbed (an empty handler is what Wolverine's `scaffold` command exists to
    write), and a string becomes a `TypeDescriptor(name, name, "")` whose empty assembly makes the
    merge compare by `Name` (jasperfx#798), so the two spellings mean the same thing — while
    reaching for a generic over a type no stub declares simply would not compile.
  - **`CSharpModelWriterTests` compiles its own output with Roslyn, loads it, runs `Configure` and
    compares the descriptor to `ImportedModelMapper.ToDescriptor` of the same model.** Asserting on
    the emitted text would pass for a file with a missing brace, a shadowed namespace or a generic
    naming a type no stub declares — all three of which happened. The round-trip is a real guard,
    checked by dropping `.Emits` and watching it go red; `Bobcat.EventModel.Tests` therefore
    references `Microsoft.CodeAnalysis.CSharp`.

It is a plain JasperFx command host and hosts no server. **Its assembly is `Bobcat.Cli`, not
`bobcat`** — see the resident-runner section for why that is load-bearing rather than cosmetic; the
command a person types is still `bobcat`. **`watch-event-model` is not a command of this tool**; it
lives in Stoat's `Stoat.Console`.

**The runner publishes the spec half of the Event Model** (issue #294,
`Monitoring/SpecEventModelPublisher.cs`). A model has two producers compiled into different
assemblies: the host's `event-model --url` export carries the slices but no `Specifications`,
while the generator's `BobcatEventModelSource` carries the spec identities a run outcome joins on
and is invisible to the host. **A push names its SOURCE and replaces only that source; `GET`
serves the merge** (issue #268) — latest-wins erased one producer every time. So a run that
attaches to a console PUTs its spec assemblies' generated descriptors under a source named for the
assembly, under the same never-slow-never-fail invariant as the event pump, and *refuses* when the
console is serving a differently-named model: `GET` merges only the sources naming the current
one, and publishing anyway would hide the other half rather than join it. Both halves of the merge
are keyed on the model name, which is why setting it explicitly is load-bearing rather than
cosmetic.

- **Every Event Model PUT says where its half came from (bobcat#532, stoat#92)**: optional `root`
  / `repository` / `branch` query parameters from `CheckoutProvenance` (`Monitoring/`), so Stoat can
  call a half from a different repository or checkout than the newest one stale. The run reads the
  working directory's checkout; `import-event-model --url` reads its `--out` directory's.
  **Named `CheckoutProvenance`, not `EventModelProvenance`** — JasperFx.Events already has a type of
  that name in `JasperFx.Events.EventModeling`, and a file that imports both stops compiling.
  Read from files, never a `git` process (`GitInfo.FindCheckout`: a worktree's config is in its
  `commondir`), and a field that can't be read is left off: a detached HEAD sends no `branch`
  (unlike `run_started`, which reports the short sha), because a sha compared as a branch would call
  a half stale that is not.

**A supervised suite that is not Bobcat's still gets live progress (issue #195).** Per-scenario
events come from each *worker's* own recorder, so a plain xUnit worker published
nothing and a supervised run's card sat at `0 / 1627` for its whole five minutes — the shape of a
wedged run. `SupervisorRunPublisher` forwards `ISupervisorObserver.TestUpdated` as **`test_started`
/ `test_finished`**, a deliberately separate, lower-fidelity pair: `scenario_finished.Uid` keeps
its documented meaning as spec identity (`{Feature}/{Scenario}`, what a `SpecificationDescriptor`
joins on), while these carry the *worker's* test id. No spec semantics are implied — projecting
foreign specs into the Bobcat model is #110, not this.

- **The supervisor forwards for every worker, Bobcat ones included**, because it cannot tell which
  of them publishes without a marker only new workers would carry. The gate that makes one test
  one card in either arrival order is a property of the *scenario* and lives on the receiving
  side, which is Stoat's now — see `docs/monitor-design.md`, where the receiving-side folds are
  kept deliberately, because a publisher that does not know how its events are read cannot tell an
  additive change from a breaking one.
- **`State` is the framework's own word** — Passed / Failed / Error / Skipped / Timeout /
  Cancelled — and travels verbatim as a string, never re-labelled by the publisher: two enums
  meaning the same thing is how a vocabulary drifts. **`Indeterminate` never reaches the wire**;
  silence is not a verdict, so a crashed run cannot read as a complete one. `DurationMs` is
  measured on the supervisor's own clock and is null when it never saw the start — unmeasured is
  never zero. `Lane` is null for a one-test isolated or recycled process, and discovery is never
  tapped.
- **`SpecId` carries the spec identity beside the uid (issue #515), and that is what makes the
  fold exact.** The receiving fold needs the two streams to name the same test, and in the
  projected lane they never did — `test_*.uid` is the MTP node uid (a hash) while `scenario_*`
  carries `{Feature}/{Scenario}` — so Stoat matched them by **method name**, ambiguous across
  classes and overloads. The identity is read from the suite's own `SpecManifest` at discovery
  through **`SpecUidJoin`**, which is `WarmProjectedResidentSuite`'s identity → uid lookup
  (#394/#470) extracted so both directions are one rule: two copies is how the end that runs a
  specification and the end that reports it would start naming different tests. Trailing and
  optional, so no wire version bump and no change for a reader that predates it. **Null is the
  honest answer** for a plain xUnit test, a suite that writes no manifest, a front-end whose
  discovery places nothing, and an identity more than one test claims (#489 — the manifest keeps
  one entry, so stamping it would name one of two tests that both are it, and `SpecFilterArguments`
  refuses such an identity for the same reason). `ReadWorkerRecoveryHints` is the gate, because it
  is the gate on reading the manifest at all; its name is kept because it is public API.

### A cell says exactly one thing on the wire (issue #396)

`StepCell` carries four content fields and a cell fills **one** of them: `Expected`/`Actual` for a
judged cell, `Note` for one that says a sentence (the set comparer's missing/extra/out-of-order
rows), and **`Value`** for one that was *not judged* — a decision table's input column.
`Bobcat.Monitoring.StepCells.From` is the single projection that decides. Before `Value`, a literal
reached a consumer as `{"name":"x","status":"ok","expected":null,"actual":null}` — there was
nowhere to put `CellResult.DisplayText` — and the column rendered empty in every grid while the
console showed it perfectly.

**`Value` is not a reuse of `Actual`, and that is load-bearing.** A cell with neither an expected
nor an actual is how `JsonRenderer` decides an input column earns no Status column (#384) — so
borrowing `Actual` for a value makes every input cell look judged, and broke that test when tried.
The plain-value `CellResult` constructor therefore stays the right one for an input cell; what was
missing was a field on the wire, not a different field in the model. `CellResult.DisplayText` also
gained a branch: a `failed` cell with no pair renders its `Note` rather than `expected '', got ''`,
which is what had kept the set comparer's out-of-order cell on the legacy constructor.

### The resident runner (`src/Bobcat/Residency/`, issue #390)

A suite kept available to a monitor, running specifications **when the monitor asks** — the Bobcat
half of Stoat's interactive execution. It ships as `bobcat resident <host>`; `ResidentMode.Run` is
the loop, and `BOBCAT_RESIDENT` / `--resident` are the request's spellings (cleared for a child,
below).

- **The runner is a client, and that is the whole security model.** It connects *out* to the same
  5525 origin every publisher already probes and *asks* for work; a monitor can only answer a
  runner that asked. Nothing listens on a port, so there is nothing to secure — and a command can
  only name specifications the runner already told the monitor it has, so the worst a hostile
  monitor can do is ask for a test run.
- **The publisher's invariant applies unchanged**: a monitor that is absent, slow or hostile never
  matters. No monitor means an idle process that keeps asking on a **capped** backoff (it may come
  up later, and nothing will relaunch the runner just because it did). A dropped stream reconnects
  with `Last-Event-ID`. Anything unparseable — a proxy's HTML error page, a command type from a
  newer console — is *ignored*, which is why `CloudEvent.FromJson` and `DataAs<T>` both return null
  rather than throwing. `ResidentRunner.Run` returns and never throws: a resident runner that died
  of a network blip would be worse than one quietly waiting, because its parent only relaunches it
  on a source change.
- **One command at a time, rejected rather than queued.** The monitor owns the queue; a runner that
  silently queued would leave a person waiting on a run whose turn they cannot see. **`run_finished`
  does not mean the runner is free** — the bracket closes inside the run, a hair before the
  in-flight slot clears, so a command sent the instant it arrives can still be refused as busy, and
  a client that wants a second run sends again. Invisible on a fast machine, which is why it is
  written down: the end-to-end test failed on CI three tags in a row over it. The three
  refusals are a foreign spec identity (**named**, from `SpecSelection.NotIn`), busy, and a mode
  this runner did not register — the last **never silently downgraded**, because someone who asked
  for warm and got cold would read the resulting wall clock as warm mode not working. A fourth:
  a command naming *no* specification is refused rather than read as "run everything", since the
  whole suite is what an ordinary run already does. Every refusal carries a reason, because a
  command that is simply never answered is indistinguishable from a runner that died.
  - **And a machine-readable `refusal` beside the reason (issue #400)** — `RunnerRefusal.Busy` /
    `UnknownSpec` / `UnsupportedMode` / `Empty` on `RunnerAcknowledgement`, trailing and optional
    so an older console ignores it. The split it encodes is that **busy means *not now*** and the
    other three mean *not this*: busy is the ordinary answer to a command sent the instant
    `run_finished` arrives, and the right response is to send again. With only the prose to go on,
    Stoat was matching the sentence "already running a command" — so a rewording here would have
    quietly turned every busy into a hard rejection a person reads as a failed button.
- **Cold by default** — every command launches a fresh child, so a command always runs the current
  code and a second cannot see the first's state because none of it survived. **Each command is its
  own run on the wire** — `run_started` … `run_finished`, its own `RunId`, carrying its command
  (#392).
- **`IResidentSuite` is the seam**, the same shape of decision as the supervisor's
  `IWorkerClient`: the protocol knows identities and modes, the suite knows how to run anything.
  `OutOfProcessResidentSuite` (cold) and `WarmProjectedResidentSuite` (cold and warm) implement it.
  A source change restarts the runner, so a suite's identity list describes exactly the code the
  process was built from.
- **`OutOfProcessResidentSuite` is the cold implementation (issue #399).** It holds the suite's
  `SpecManifest` and runs a command by launching **the suite's own test host** with
  `SpecFilterArguments.For`'s translation of the selection. A projected suite's entry point belongs
  to xUnit or TUnit, which will never learn what `--resident` means — so a resident runner is not
  the suite. Everything it does is a function of the manifest. **This class is cold-only and stays
  so** — it is in core, and warmth needs the supervisor's MTP client; `WarmProjectedResidentSuite`
  is what offers both (below).
  - **It ships as `bobcat resident <host>`, a command on the free tool**, because the console at
    the other end references nothing in this repository and so cannot be handed a class to host.
    That forced the tool's assembly to be renamed **`Bobcat.Cli`**: it now references core, and
    `bobcat.dll` beside `Bobcat.dll` is **one file** on a case-insensitive filesystem — on macOS
    the tool's own assembly overwrote core's in the output directory and every call into it died
    with `Could not load type … from assembly 'bobcat'`, while the same build was fine on Linux and
    would have shipped green from CI. `ToolCommandName` is still `bobcat`, so nothing a person
    types changed.
  - **A wiring mistake is refused at launch, not at the first button press**: a host that is not
    built, a suite that lists no specifications (usually a spec project with no runner adapter
    referenced), or a framework whose filter spelling Bobcat will not guess at (NUnit, MSTest). A runner
    that registered and then refused every command would look broken rather than unsupported.
  - **What the child does *not* inherit is the substance of it** (`EnvironmentFor`). Four variables
    a parent routinely sets break a commanded run silently: `BOBCAT_RUN_ID` collapses every command
    into one ever-growing run card, `BOBCAT_RUN_OWNER` stops the child publishing a run bracket at
    all, `BOBCAT_LIST_SPECS` turns a run into a listing, and `BOBCAT_RESIDENT` would have the child
    register itself instead of running (no projected host reads it today, which is exactly why it
    is cleared rather than relied upon). `BOBCAT_MONITOR=1` is *forced on* — a resident runner
    exists to serve a console, and `BOBCAT_MONITOR=0` is the kind of thing a CI job sets for a whole
    box — and `MonitorUrl` travels down as `BOBCAT_MONITOR_URL`, because a runner pointed at a
    second console with `--url` would otherwise take that console's commands and publish the runs
    to the default one. It travels **whole**, list and all (bobcat#530), while the runner registers
    with the first entry only.
- **Warm is `WarmProjectedResidentSuite`, in `Bobcat.Supervisor` (issue #402 item 4).** It wraps
  `OutOfProcessResidentSuite` — **cold is delegated unchanged**, so a cold command is byte-for-byte
  what it always was — and holds an `MtpWorkerClient` open for warm, so a warm command is one
  `testing/runTests` request into the live process. `bobcat resident` registers `cold` and `warm`; a
  console chooses per command, so nothing changes for anyone who never asks for warm.
  - **`BOBCAT_RUN_COMMAND_FILE` is the per-request command channel, and a new public variable.**
    A warm child's environment is **fixed at launch**, so `BOBCAT_RUN_COMMAND` cannot serve: every
    command after the first would carry the first one's id — precisely the mis-attribution #401
    was opened to fix. Nor is there a protocol slot: measured on MTP 1.9.1, the `runId` a client
    sends on `testing/runTests` is the client's own and is **not** the `SessionUid` the session
    handler receives (unrelated GUIDs). So the parent writes the id to a path fixed at launch
    **before** sending each request — **the exact inverse of `BOBCAT_LIST_SPECS`**, where the child
    writes what the parent reads. No race, because a resident runner runs one command at a time and
    refuses rather than queues. It **wins over `BOBCAT_RUN_COMMAND`** as the narrower claim, every
    read failure falls back to the variable and then to null (attribution must never fail a run),
    and `EnvironmentFor` **clears** it for a cold child alongside the four it already clears — a
    parent's stale file must never be read as this command.
  - **Layering: the warm suite moved UP, the client did not move DOWN.** `MtpWorkerClient` stays in
    `Bobcat.Supervisor`, because **core is what every spec project references** and an MTP JSON-RPC
    client there would tax all of them for a capability only the resident tool uses. `IWorkerClient`
    is documented as *the* seam for exactly this and #402 anticipated "a second caller of
    `IWorkerClient` rather than a new transport", so the second caller sits on the supervisor's side
    of it. `Bobcat.Console` → `Bobcat.Supervisor` is honest — driving a test host as a process is
    that tool's whole job here. (Watch the `Bobcat.Cli` assembly name while in that csproj:
    `Bobcat.Supervisor.dll` beside `Bobcat.Cli.dll` is fine, where `bobcat.dll` beside `Bobcat.dll`
    was one file on a case-insensitive filesystem.)
  - **The identity → uid join is one lookup**, per #394: the manifest's `QualifiedTestMethod`
    against the discovery display name, with `Ns.Outer+Inner` → dotted tried as a *fallback* after
    the exact match #394 verified. An identity whose uid cannot be named is **refused**, not dropped
    from the subset — a request for three specs that silently ran two looks exactly like a pass.
    **TUnit joins on the location instead (bobcat#470):** its display name is the bare method name,
    while `location.type` / `location.method` are the manifest's `TestClass` (`Outer+Inner`) and
    `TestMethod` exactly, and a data-driven method maps one identity to several uids. That is why a
    warm TUnit command can run any set of identities while a cold one is limited to what one
    tree-node path can name. `Bobcat.Supervisor.Tests/TUnitResidentSuiteTests` drives both against
    the real `Bobcat.TUnit.Tests` host.
  - **What warmth buys is the CLR, not the boot**, and the Q2 caveat stands word for word: xUnit's
    assembly and collection fixtures are created and disposed *within* a run request, so a second
    request pays for them again. A suite whose cost is a collection fixture standing up a database
    will see far less than #394's 72ms → 4ms.
  - **Withdrawal**: a faulted warm session drops `warm` from the registered modes, re-registers so
    the monitor stops offering a button that would now be refused, and refuses a warm command *with
    that reason*. **Cold is unaffected** — it starts over from exactly the thing that poisoned the
    warm host — and a cold command **closes the warm session first**, since a booted host holds the
    port, the database and the queues.
  - `Bobcat.Supervisor.Tests/ProjectedResidentRunnerTests` holds the warm tests **beside the cold
    ones**: two modes from one class, so neither gains a rule the other never hears about. One of
    them re-learned a documented rule the hard way — **`run_finished` does not mean the runner is
    free**, the bracket closing a hair before the in-flight slot clears — so a second command sent
    the instant it arrived was refused `busy`. The test waits for `ResidentRunner.Busy` to clear,
    which is what a real client's "send again" amounts to.
- **`restart` means "exit so I can be relaunched"**, and it **cuts an in-flight run short** rather
  than waiting: a wedged run is the main reason someone restarts a runner, so a restart that waited
  would be useless in exactly the case it exists for. **A restart exits 75 (`EX_TEMPFAIL`), an
  orderly stop exits 0 (issue #397).** Neither says anything about any test — the verdicts went out
  on the ingest stream as they happened, so a parent deciding whether to relaunch never has to tell
  a red suite from a crashed runner. But it does have to tell "relaunch me" from "I'm done", and
  0 could not: **0 is also what a non-resident host returns after running its whole suite**, so a
  parent relaunching on 0 would run such a suite in a loop forever. Stoat had to ask the monitor
  whether the launch had registered to work it out. `ResidentMode.RestartExitCode`, and the
  `bobcat resident` command carries it out through its input rather than JasperFx's true/false,
  which cannot say a third thing.
- **`BOBCAT_RUNNER_ID`, when set, *is* the runner's id (issue #397)** — `ResidentRunnerOptions`
  defaults to it and only then to a fresh GUID. A runner lives under a watch and is relaunched on
  every source change, so a minted id per start meant a new runner per rebuild: a command a person
  pressed while the runner was rebuilding waited on an id that never came back, the monitor's
  picker filled with dead runners, and a parent's status reports named an id the runner never
  registered under. Stoat worked around all three by superseding a disconnected runner from the
  same checkout and polling `GET /api/runners` for the minted id; handing the id over is the exact
  version of the same thing. Re-registering is idempotent, which is what makes a stable id safe.
- **Signals go through `PosixSignalRegistration`, not `ProcessExit`.** A `ProcessExit` handler runs
  after `ResidentMode.Run` has returned and disposed the `CancellationTokenSource` it would cancel,
  so the `ObjectDisposedException` aborts the process: **exit 134 from a runner that had done
  everything right**. SIGINT/SIGTERM/SIGQUIT are each an orderly stop.
- **The wire is CloudEvents, structured mode, hand-written** (`CloudEvent`, `RunnerWire`). No
  package: six scalar attributes and a payload, and the *wire shape* is the contract — the same
  standing as `Bobcat.Monitoring.MonitorEvents`, pinned by `RunnerWireContractTests` rather than by
  an assembly reference. **Ids are GUIDs** because the receiving side's mapper replaces an id it
  cannot read as one with a fresh one, which would silently break a correlation; correlation
  therefore lives in `data` (`commandId`) and never in the event id alone. Payloads are camelCase,
  because the reader is a .NET web host whose default is camelCase.
- **`ServerSentEvents` is hand-rolled**, and the reason is the TFM matrix:
  `System.Net.ServerSentEvents` is in the box on **net10.0 only** and Bobcat also targets net9.0, so
  the choice was a package on one target and a framework type on the other, or one implementation of
  the four fields used. It honours `id` (persisting across events that omit it), `event`, repeated
  `data` joined with newlines, comment lines, one optional space after each colon, and a trailing
  event with no blank line after it. **`retry` is read and dropped** — the reconnect delay is the
  runner's own backoff, so a monitor cannot make a runner reconnect in a tight loop.
- **A commanded run leaves `Session` null, and that is one rule rather than a flag (issue #401).**
  A resident runner started from an agent's terminal inherits `CLAUDE_CODE_SESSION_ID` and holds it
  for its whole life, so every run it made for a monitor command was stamped with the session that
  launched the *runner* — and Stoat's agent page said "started by this session" about runs a person
  had pressed in the UI. The fix is on `MonitorRunInfo`: `Session`'s getter returns null whenever
  `Command` is set, so a `with { Command = … }` anywhere honours it and an out-of-process
  child gets it for free from the variable. `Command` (#392) is the true answer to who asked;
  **`Tag` is unaffected**, because it answers a different question — what work the run speaks for —
  and a commanded re-run of a slice's specs is still that slice's node's.

`ResidentRunnerTests` drives the whole loop against `FakeMonitorHost` over **real HTTP**, because
the invariant under test is about an absent, slow or hostile monitor and a seam in front of the
transport would test the seam instead. `Bobcat.Supervisor.Tests/ProjectedResidentRunnerTests` goes
further: the real `Bobcat.Xunit.Samples` xUnit host, listed and then filtered to one specification
by identity, with the `bobcat resident` executable itself driven as a process so that a class
nothing ships is not mistaken for a feature. **Everything in this repository is proved against a
stand-in console** (`FakeMonitorHost` — real HTTP, not a real consumer), which is the honest limit
of what can be pinned here: Stoat references nothing in this repository and this repository
references nothing in Stoat, so neither side's CI can boot the other. The live half was done once,
on Stoat's side (stoat@0ecd122, 2026-10-04): `Stoat.ProjectedSpecs` through `bobcat resident` ran a
feature's 7 scenarios exactly. Two things only a live run could surface came out of it, both fixed:
a runner id that was per checkout rather than per project (Stoat's side), and `run_finished` not
meaning the runner is free.

## Bobcat is MIT; AI agent coordination lives in Stoat

Split 2026-08-09, finished 2026-09-18 when the console followed. **Bobcat is the MIT integration
testing framework** — source generator, specification recorder, runner adapters, supervisor, and the
*publisher* side of the monitor wire. **[Stoat](https://github.com/JasperFx/stoat) is the BSL AI
agent coordination tool** (cross-repo plan DAGs, GitHub/NuGet observation, agent claims, MCP), and
it now hosts the run console as well. The viewer's `Coordination/` folder went first; the rest of
the viewer went in 3ee3db9.

Two rules this creates, both load-bearing:

- **Nothing here may reference Stoat, and Stoat references nothing here.** Not one-way — *zero*.
  The two sides meet only on the wire: a run POSTs `/api/ingest` and PUTs `/api/event-model` at
  whatever `BOBCAT_MONITOR_URL` names — every origin it names, since bobcat#530, which is how
  Stoat and `bobcat ui` watch one run without either knowing about the other — on the same terms
  Stoat observes GitHub and nuget.org. An
  MIT repo cannot depend on a BSL one, and keeping the dependency at zero makes that impossible to
  get wrong by accident.
- **No license gating in this repo.** Bobcat is free, entirely. If a feature seems to want a
  gate — or needs to remember something across a process — it belongs in Stoat.

What a run publishes is therefore a **public wire contract**, not an internal detail: the events in
`src/Bobcat/Monitoring/MonitorEvents.cs` are read by a console that has no assembly reference to
warn it, so changing one of their shapes breaks an external consumer silently. `BOBCAT_RUN_TAG` is
the correlation hook — an opaque string Bobcat stamps on a run and never interprets (it was
`BOBCAT_PLAN_NODE`, renamed in the split because coordination vocabulary does not belong here).

## Key Dependencies

- **JasperFx** — Utility library (core Bobcat). Source at ~/code/jasperfx
- **JasperFx.Events** — the store abstractions the Critter Stack helpers bind to
- **Spectre.Console** — Terminal rendering
- **Microsoft.CodeAnalysis.CSharp** — Roslyn (generator only, compile time)

## Consuming Project Setup

```xml
<PackageReference Include="Bobcat" />
<PackageReference Include="Bobcat.Generators" />
<PackageReference Include="Bobcat.Xunit" />   <!-- or Bobcat.TUnit / Bobcat.NUnit / Bobcat.MSTest -->
```

The `Bobcat.Generators` package's `buildTransitive/Bobcat.Generators.props` opts the project into
`InterceptorsNamespaces=Bobcat.Generated` (without it, BOBCAT037). A `ProjectReference` to the
generator (`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`) takes no build assets, so
in-repo projects import that props file — and `Bobcat.Xunit.props` — themselves, the way
`samples/Directory.Build.props` does. `BobcatProjectAssertions=true` opts a project into projected
Shouldly assertions.

## Design References

- `docs/` — the user-facing docs: `getting-started.md` (package setup), `marker-steps.md` (writing
  specifications), `xunit.md` / `tunit.md` / `nunit.md` / `mstest.md`, `spec-identities.md` (#391
  and #338's `SpecIdentityAudit`), `resident-runner.md`, `warm-projected-runs.md` (#394's findings),
  `parallel-ready-suites.md`, `bobcat-tool.md`, and `monitor-design.md` (the wire contract and the
  receiving-side folds)
- `docs/tutorials/` — `specifications-with-code.md`, `data-intensive-specifications.md` (tables,
  set verification, decision tables, cell expressions and the four row markers),
  `agent-friendly-tests.md`, `event-modeling.md`, `continuous-integration.md`,
  `ide-integration.md`, `reliable-integration-testing.md`
- `design/` — internal records: `ledger-design.md` (the committed ledger),
  `critter-stack-interop-notes.md`, `versions.md`, `wolverine-ci-rollout.md`; `design/history/`
  keeps the Gherkin-era records (`spec-driven-development-design.md`, `acceptance-coverage-map.md`,
  `sample-wiring-playbook.md`)
- JasperFx source at ~/code/jasperfx
