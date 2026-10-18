# Decoupling Bobcat from JasperFx and Wolverine (#744, #745)

Status: **decided**, 2026-10-18 (Jeremy). Breaking changes are fine before 1.0.

1. **`Bobcat` and `Bobcat.Supervisor` take no dependency on JasperFx or JasperFx.Events.** This reverses #63 and #103.
2. **Nothing in Bobcat references Wolverine, with one exception (`Bobcat.Ui`, below).**
   - The Critter Stack vocabulary moves into **WolverineFx.Bobcat** (Wolverine repo, `src/Bobcat/Wolverine.Bobcat`),
     not into a new Bobcat package. That package may be renamed soon. For now it holds the JasperFx.Events coupling.
   - **All samples move to the Wolverine repo**, along with what builds and runs them (`build/Samples.cs`,
     `samples.yml`, the samples' compose file and init scripts).
   - **`import-event-model --specs` moves to Wolverine.** It writes WolverineFx.Bobcat specs and a spec project, and it
     checks the app's Wolverine setup. Bobcat keeps the board → stubs + `EventModelDefinition` import. Writing specs
     becomes a Wolverine-side command, next to `wolverine scaffold`.
3. **`Bobcat.Ui` keeps both couplings**: JasperFx, and WolverineFx.Http/SignalR/RuntimeCompilation. It is a tool, and
   its graph never reaches a consumer's restore.
4. **The generator keeps recognizing Wolverine.Http endpoint attributes by metadata name** for act routes (#500).
   This has the same standing as its MVC recognition: no reference, nothing restored.
5. The trigger enum is **`ActTrigger`**, Event Model publishing goes through **the generated bridge**, and **#745
   follows #744** in its own PR.

## Why

A test framework shouldn't pin its users' application framework. Bobcat's JasperFx floor (2.90.2 at rc.1) shows up in
every consumer's restore:

- NU1605 and NU1109.
- Low pins that resolve silently (#740).
- A `VersionOverride` in the build projects of Marten, Polecat, Weasel, Fisher, CritterWatch and Wolverine (#565's
  adoption PRs).

The Supervisor is the worst case. It drives test hosts as processes and never needs JasperFx at all.

## What core touches today (origin/main, ff7d15a)

| Area | Files | JasperFx surface | Where it goes |
|---|---|---|---|
| Critter Stack vocabulary | `CritterStack/` `CritterStackScenario`, `CritterStackSpec`, `CritterStackActOutcome`, `EventStores`, `DocumentStores`, `StoreRouting`, `EventStoreAuthoring`, `CritterStackHostExtensions` | `JasperFx.Events` (`.Tags`, `.Daemon`, `.Projections`, `.Documents`, `.Descriptors`), `JasperFx.Resources`, `JasperFx.CommandLine.Descriptions` | **WolverineFx.Bobcat** |
| Store-free helpers in the same folder | `RecordBuilding`, `ProjectionsBehind`, `ActTimedOutException`, `ProjectionCatchUpTimeoutException` | none | `RecordBuilding` **stays in core**, because `TableRunner` calls it. The other three are only thrown or returned by the vocabulary, so they move with it. |
| Event Model reading | `EventModel/GeneratedEventModel`, `EventModel/SpecIdentityAudit`, `SpecificationBindings` (already `[Obsolete]`) | `EventModelDescriptor`, `IEventModelDefinitionSource`, `SpecificationManifest` | `GeneratedEventModel` and `SpecIdentityAudit` move to **WolverineFx.Bobcat**. `SpecificationBindings` and Bobcat's old `SpecificationManifestAttribute` are **deleted** (obsolete since #528). |
| Event Model publishing | `Monitoring/SpecEventModelPublisher`, called from `MarkerStepRun` on attach | `EventModelDescriptor` (serialized to JSON) | **stays in core**, but it is handed JSON through the generated bridge |
| Vocabulary role attributes | `EventModel/EventModelRoleAttribute.cs` (`EventModelRoleAttribute`, `EventModelTriggerAttribute`, `SliceRole`) | `TriggerKind` only. `SliceRole` is already Bobcat's own type. | **stay in core**, with `ActTrigger` replacing `TriggerKind` |
| Resilience | `Resilience/Disposition`, `DefaultFailurePolicy`, `HintedFailurePolicy`, `RecoveryHint`, `Runtime/SpecManifestHint` | `JasperFx.Testing.DispositionKind` and the four recovery-hint attributes | **Bobcat owns both in core**, namespace `Bobcat.Resilience` |
| Preflight | `Runtime/Preflight` | `EnvironmentCheckResults`, `EnvironmentChecker` | Bobcat `PreflightResults` in core. `AddContainerChecks` moves to **WolverineFx.Bobcat**. |
| Rendering | `CommandLineRenderer`, `RenderingClasses` | `JasperFx.Core` extension helpers | inline the few calls |

Every mention of Wolverine in `src/Bobcat` and `src/Bobcat.Supervisor` is in a comment. The comments that describe
WolverineFx.Bobcat's behaviour go; those that cite a Wolverine issue as history stay.

## The shape

```
Bobcat                 (no JasperFx, no Wolverine)
                       projected engine, verification, partial objects, RecordBuilding, rendering, monitor wire,
                       resident runner, Resilience types, Preflight, role attributes + ActTrigger
Bobcat.Supervisor      → Bobcat         (no JasperFx, so #745 falls out of #744)
Bobcat.Xunit/TUnit/NUnit/MSTest → Bobcat   (unchanged, already clean)
Bobcat.Generators      (netstandard2.0) unchanged references; recognizes Wolverine.Http/MVC attributes by name;
                       gains the publishing bridge
Bobcat.EventModel      → JasperFx.Events   board → stubs + EventModelDefinition only (the spec writer leaves)
Bobcat.Console, Bobcat.Ui   tools: Ui keeps JasperFx + WolverineFx.Http/SignalR
WolverineFx.Bobcat     → Bobcat, JasperFx.Events, Wolverine
                       + CritterStackScenario/Spec, EventStores, routing, GeneratedEventModel, SpecIdentityAudit,
                         AddContainerChecks, ClearStatefulResourcesAsync, the spec writer, the samples
```

- **The namespace stays `Bobcat.CritterStack`** inside WolverineFx.Bobcat. A spec that already references
  WolverineFx.Bobcat changes nothing.
- **The cost:** a store-only suite (Marten, Polecat or Fisher with no Wolverine, #471's Option A) now references
  WolverineFx.Bobcat and so takes Wolverine transitively. That is accepted for now, and WolverineFx.Bobcat's planned
  rename is where it would be revisited.
- `WolverineScenario : CritterStackScenario` becomes an inheritance inside one assembly. The protected seams stay.

## What leaves the Bobcat repo

| What | Goes to | Notes |
|---|---|---|
| `src/Bobcat/CritterStack/*` (minus `RecordBuilding`), `GeneratedEventModel`, `SpecIdentityAudit` | WolverineFx.Bobcat | Same namespaces. |
| `src/Bobcat.CritterStackScenario.Tests` | beside `Wolverine.Bobcat.Tests` | Keeps `[PostgresFact]` (the never-skip-on-CI rule) and the Fisher temp-file host. |
| `SpecIdentityAuditTests` (in `Bobcat.Tests` and `Bobcat.Projected.Acceptance.Tests`) | with the audit | |
| `samples/*` (all 13), `build/Samples.cs`, `.github/workflows/samples.yml`, `samples/docker-compose.yml`, `samples/init` | Wolverine repo | Consume Bobcat as a package. BankAccountES's Event Model pins (#300, #297, the GET-as-View disagreement) and its Marten/Fisher switch go too. |
| `Emlang/EmlangSpecWriter`, `Emlang/SpecProjectWriter`, `--specs` / `--bobcat-version` / `--wolverine-version` on `import-event-model` | a Wolverine command | `ImportMerge` keeps merging stubs and declarations here. Its "missing spec" arm goes with the spec writer. |
| The `artifacts/local-feed` NuGet.config that BankAccountES uses | Wolverine repo | `Build.cs`'s comment about it is updated. `Pack` and `Clean` don't change. |

**What stays here and is rewritten:**
- `TagsAndStrongTypedIdsTests` and the two acceptance specs that touch the vocabulary are rewritten against core types.
- The generator tests compile a fake `JasperFx.Events` surface, so they stay.
- `ResourceParsingAgreementTests` is deleted, because there is now one parser.

## The publishing seam: the generated bridge

The generator already emits `BobcatEventModelSource` only when the consumer has JasperFx.Events. It also emits one
module-initializer line that hands core an opaque producer:

```csharp
global::Bobcat.Monitoring.SpecEventModels.Register(
    modelName: "BankAccountES",
    source: "BankAccountES.Tests",
    json: static () => /* TryCreateAsync → WithProvenance → JsonSerializer with the Web defaults */);
```

- `SpecEventModelPublisher` publishes `(modelName, source, json)` triples. It still refuses a different model name, as
  #294 requires.
- A test pins today's JSON for a fixture model, then the bridge's output against it.
- `SpecIdentityAudit` (#338) needs the real descriptor, so it moves with `GeneratedEventModel`.

## `ActTrigger`

`EventModelTriggerAttribute(ActTrigger kind)` replaces `TriggerKind`, with the same member names. The generator matches
`"Http"` by name, so the emitted descriptor is unchanged. A distinct name avoids CS0104 in WolverineFx.Bobcat's
vocabulary files, which import `JasperFx.Events.EventModeling`.

## Resilience

`DispositionKind` and `[ClearsOnRetry]` / `[ClearsOnRecycle]` / `[ClearsInFreshProcess]` / `[NeverRecovers]` move to
`Bobcat.Resilience` in core, and the worker reads them as before (#469).

- No adapter for JasperFx.Testing's copies: nothing outside JasperFx's own tests annotates with them (checked across
  wolverine, marten, polecat, fisher, weasel and CritterWatch).
- JasperFx follow-up: mark them `[Obsolete]`, pointing at Bobcat.
- The wire carries the disposition as a string, so the wire doesn't change.

## Preflight

- `Preflight.Run` returns a Bobcat `PreflightResults` (successes, failures, `Succeeded`, `Assert()`), shaped like
  `EnvironmentCheckResults`.
- `ITestResource.Check` keeps mirroring `IStatefulResource.Check` by shape.
- `AddContainerChecks(IServiceProvider)` moves to WolverineFx.Bobcat.

## Breaking changes (rc.2 migration notes)

1. `CritterStackSpec`/`CritterStackScenario`, `EventStores`, `DocumentStores`, `SpecIdentityAudit`,
   `AddContainerChecks` and `services.EventStore()` are now in **WolverineFx.Bobcat**, under the same namespace.
2. `JasperFx.Testing.DispositionKind` and the hint attributes become `Bobcat.Resilience.*`.
3. `Preflight.Run` returns `PreflightResults`.
4. `[EventModelTrigger(TriggerKind.Http, …)]` becomes `ActTrigger.Http`.
5. `SpecificationBindings` and Bobcat's `SpecificationManifestAttribute` are deleted.
6. `bobcat import-event-model --specs` is gone. Specs come from the Wolverine-side command.
7. A suite that got JasperFx **transitively through Bobcat** and uses it directly now needs its own reference.

## Sequencing

The two repos must not leave a window where nobody ships the vocabulary:

1. **Wolverine PR, prepared against Bobcat rc.1 and not merged.** It contains:
   - The vocabulary, the audit, the tests, the samples with their build target and workflow, and the spec writer.
   - Against rc.1 the vocabulary types would be declared twice (CS0433), so it waits for step 3.
2. **Bobcat #744 PR:**
   - Delete everything in the "leaves" table.
   - Add `Bobcat.Resilience`, `PreflightResults`, `ActTrigger` and the publishing bridge.
   - Inline the `JasperFx.Core` helpers.
   - Add a guard test that `Bobcat.dll` references no `JasperFx*` or `Wolverine*` assembly.
   - Remove the Nuke `Samples` targets, so `samples.yml` leaves with them.
   - Update the docs and the ai-skills entry in the same piece of work.
3. **Publish Bobcat rc.2.** The Wolverine PR bumps to rc.2 and merges, and WolverineFx.Bobcat ships.
4. **#745 Supervisor PR:** three files switch to `Bobcat.Resilience.DispositionKind`, plus the guard test for
   `Bobcat.Supervisor.dll`.
5. **Follow-up PRs** dropping the `VersionOverride`s in Marten, Polecat, Weasel, Fisher, CritterWatch and Wolverine
   build projects.

## Decisions this reverses (logged in `bdd-tools-comparison.md`)

- **#63:** the recovery-hint attributes lived in JasperFx so a suite could annotate without referencing Bobcat. Nobody
  used that, and the price was a JasperFx floor in every consumer.
- **#103 and #471 Option A:** the store-agnostic helpers lived in Bobcat core so a store-only suite needed no Wolverine.
  They now ship from WolverineFx.Bobcat until its rename settles their home.
- **#693:** "no split for 1.0" is superseded.
- **The samples as Bobcat's acceptance proof** (`samples.yml`, #103's "runs on Marten and Fisher"): that proof now
  runs in the Wolverine repo.
