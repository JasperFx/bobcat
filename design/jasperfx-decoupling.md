# Decoupling Bobcat from JasperFx (#744, #745)

Status: **design note**, 2026-10-18. Decisions by Jeremy the same day:

- `Bobcat` and `Bobcat.Supervisor` take no dependency on JasperFx or JasperFx.Events. Breaking changes are fine before
  1.0. This reverses #63 and #103 (see "Decisions this reverses").
- **The Critter Stack vocabulary moves into WolverineFx.Bobcat** (the Wolverine repo, `src/Bobcat/Wolverine.Bobcat`),
  not into a new Bobcat package. That package may be renamed soon. For now it is where the JasperFx.Events coupling
  lives.
- **`Bobcat.Ui` keeps its JasperFx coupling.** It is a tool, and its graph doesn't reach a consumer's restore.

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
| Event Model publishing | `Monitoring/SpecEventModelPublisher`, called from `MarkerStepRun` on attach | `EventModelDescriptor` (serialized to JSON) | **stays in core**, but it is handed JSON instead of a descriptor (see "The publishing seam" below) |
| Vocabulary role attributes | `EventModel/EventModelRoleAttribute.cs` (`EventModelRoleAttribute`, `EventModelTriggerAttribute`, `SliceRole`) | `TriggerKind` only. `SliceRole` is already Bobcat's own type. | **stay in core**, with a Bobcat-owned trigger enum |
| Resilience | `Resilience/Disposition`, `DefaultFailurePolicy`, `HintedFailurePolicy`, `RecoveryHint`, `Runtime/SpecManifestHint` | `JasperFx.Testing.DispositionKind` and the four recovery-hint attributes | **Bobcat owns both in core**, namespace `Bobcat.Resilience` |
| Preflight | `Runtime/Preflight` | `EnvironmentCheckResults`, `EnvironmentChecker` | Bobcat result type in core. `AddContainerChecks` moves to **WolverineFx.Bobcat**. |
| Rendering | `CommandLineRenderer`, `RenderingClasses` | `JasperFx.Core` extension helpers | inline the few calls |

The generator needs no change for the move itself:

- It reads `[EventModelRole]`/`[EventModelTrigger]` by metadata name and the trigger by enum member name.
- It emits `global::JasperFx.Events.EventModeling.*` only behind its type probe on the consumer's compilation.

## The shape

```
Bobcat                 (no JasperFx)   projected engine, verification, partial objects, RecordBuilding, rendering,
                                       monitor wire, resident runner, Resilience types, Preflight, role attributes
Bobcat.Supervisor      → Bobcat        (no JasperFx, so #745 falls out of #744)
Bobcat.Xunit/TUnit/NUnit/MSTest → Bobcat   (unchanged, already JasperFx-free)
Bobcat.EventModel      → JasperFx.Events   (unchanged: the importer's library, used by the tool)
Bobcat.Console, Bobcat.Ui              tools: keep their JasperFx coupling
WolverineFx.Bobcat     → Bobcat, JasperFx.Events, Wolverine
                       + CritterStackScenario/Spec, EventStores, routing, GeneratedEventModel, SpecIdentityAudit,
                         AddContainerChecks, ClearStatefulResourcesAsync
```

- **The namespace stays `Bobcat.CritterStack`** inside WolverineFx.Bobcat. A spec that already references
  WolverineFx.Bobcat changes nothing.
- **The cost:** a store-only suite, with Marten, Polecat or Fisher and no Wolverine (#471's Option A), now references
  WolverineFx.Bobcat and so takes Wolverine transitively. That is accepted for now. The planned rename of
  WolverineFx.Bobcat is where it would be revisited, for example by splitting the store-only half back out on the
  Wolverine side.
- **`WolverineScenario : CritterStackScenario` becomes an inheritance inside one assembly.** The protected seams
  (`runActAsync`, `failureOf`, `createView`, …) stay as they are. Some could become `internal` later, but this move
  doesn't need it.

## Samples and tests that move or change

Two things in the Bobcat repo can't stay as they are, because each would need WolverineFx.Bobcat, which is built
against a *published* Bobcat. That is a cycle.

- **`src/Bobcat.CritterStackScenario.Tests`** (11 files: every vocabulary member on real Marten and real Fisher)
  **moves to the Wolverine repo** next to `Wolverine.Bobcat.Tests`, with its `[PostgresFact]` and Fisher temp-file
  setup.
- **The four samples built on `CritterStackSpec`**: BankAccountES, BookingMonolith, MeetingGroupMonolith,
  ProjectManagement. BankAccountES is also the Event Model worked example: the #300 store rung, the #297 consumed
  events, the GET-as-View disagreement pin, and the Marten/Fisher switch. The options:
  - **(a) Move them to the Wolverine repo** beside WolverineFx.Bobcat, consuming Bobcat as a package. *(recommended:
    they exercise the vocabulary, which is Wolverine's now)*
  - (b) Move them to CritterStackSamples, consuming both as packages, so CI there must run them.
  - (c) Keep them here, consuming Bobcat **and** WolverineFx.Bobcat from `artifacts/local-feed`. This brings back the
    lockstep publish that `samples.yml` was meant to avoid.
- **Smaller fallout here:**
  - `SpecIdentityAuditTests` (in `Bobcat.Tests` and `Bobcat.Projected.Acceptance.Tests`) move with the audit.
  - `TagsAndStrongTypedIdsTests` and the two acceptance specs that touch the vocabulary are rewritten against core
    types or move.
  - The generator tests compile a fake `JasperFx.Events` surface, so they stay.
  - `ResourceParsingAgreementTests` is deleted (see "Resilience").

## The publishing seam: the generated code serializes, core sends bytes

Today core loads the generated `BobcatEventModelSource` through `IEventModelDefinitionSource` (`GeneratedEventModel.For`),
stamps provenance, and serializes the descriptor. Without JasperFx, core can't name either type.

**Proposal:** the generator, which already emits the source only when the consumer has JasperFx.Events, also emits a
module-initializer line that hands core an opaque producer:

```csharp
global::Bobcat.Monitoring.SpecEventModels.Register(
    modelName: "BankAccountES",
    source: "BankAccountES.Tests",
    json: static () => /* TryCreateAsync → WithProvenance → JsonSerializer with the Web defaults */);
```

- `SpecEventModelPublisher` publishes `(modelName, source, json)` triples. It still refuses a different model name, as
  #294 requires, using the name it was given.
- The serialization moves into the consumer's compilation, which by construction references JasperFx.Events. A test
  pins today's JSON before the change and the bridge's output against it.
- The alternative is reflection over `IEventModelDefinitionSource` by name inside core. It keeps the generator
  unchanged, but it means core reflects over a JasperFx type and depends on JasperFx's JSON shape without a reference.
- `SpecIdentityAudit` (#338) needs the real descriptor, so it moves with `GeneratedEventModel` to WolverineFx.Bobcat.

## Role attributes: a Bobcat-owned trigger enum

`EventModelTriggerAttribute(TriggerKind kind)` takes JasperFx's enum. Core gets its own enum with the same member
names (the generator matches `"Http"` by name), so the emitted descriptor is unchanged.

`Bobcat.TriggerKind` would be ambiguous (CS0104) in WolverineFx.Bobcat's vocabulary files, which import
`JasperFx.Events.EventModeling`. **Recommendation: `ActTrigger`** (`[EventModelTrigger(ActTrigger.Http, Route =
nameof(route))]`). It names the act's trigger, which upstream derives and never lets anyone declare.

## Resilience: Bobcat owns the vocabulary, JasperFx.Testing's copy retires

`DispositionKind` and `[ClearsOnRetry]` / `[ClearsOnRecycle]` / `[ClearsInFreshProcess]` / `[NeverRecovers]` move to
`Bobcat.Resilience` in core. The worker reads them as it does today (#469).

- **No adapter for the JasperFx copies.** I checked wolverine, marten, polecat, fisher, weasel and CritterWatch: nothing
  outside JasperFx's own tests annotates with them.
- `ResourceParsingAgreementTests` goes: there is now one parser.
- **JasperFx follow-up:** mark `JasperFx.Testing`'s types `[Obsolete]`, pointing at Bobcat.
- The monitor wire carries the disposition as a **string**, so the wire doesn't change. `Bobcat.Ui` can keep JasperFx's
  enum or switch; it's a tool either way.

## Preflight

`Preflight.Run` returns a Bobcat `PreflightResults` (successes, failures, `Succeeded`, `Assert()`), shaped like
`EnvironmentCheckResults` so call sites change only the type name.

- `ITestResource.Check` keeps mirroring `IStatefulResource.Check` by shape.
- `AddContainerChecks(IServiceProvider)` becomes an extension method in WolverineFx.Bobcat.
- The CLAUDE.md rule "there is no Bobcat `IEnvironmentCheck`" still holds. We add a result type, not a check
  abstraction.

## Breaking changes (rc.2 migration notes)

1. `CritterStackSpec`/`CritterStackScenario`, `EventStores`, `DocumentStores`, `SpecIdentityAudit`,
   `AddContainerChecks` and `services.EventStore()` are now in **WolverineFx.Bobcat**, under the same namespace. A
   store-only suite adds that package.
2. `JasperFx.Testing.DispositionKind` and the hint attributes become `Bobcat.Resilience.*`.
3. `Preflight.Run` returns `PreflightResults`.
4. `[EventModelTrigger(TriggerKind.Http, …)]` becomes `ActTrigger.Http` (WolverineFx.Bobcat's vocabulary only).
5. `SpecificationBindings` and Bobcat's `SpecificationManifestAttribute` are deleted.
6. A suite that got JasperFx **transitively through Bobcat** and uses it directly now needs its own reference.

## Sequencing

The two repos must not leave a window where nobody ships the vocabulary:

1. **Wolverine PR first, against Bobcat rc.1.** Copy the vocabulary into WolverineFx.Bobcat under its namespace, along
   with the tests and (if (a)) the samples. While rc.1 still carries the same types, that would clash (CS0433), so
   this PR is **prepared but not merged** until step 3.
2. **Bobcat #744 PR:**
   - Delete the vocabulary, `GeneratedEventModel`, `SpecIdentityAudit` and their tests.
   - Add the Resilience, Preflight and `ActTrigger` types, and the publishing bridge.
   - Inline the `JasperFx.Core` helpers.
   - Add a guard test asserting that `Bobcat.dll` references no `JasperFx*` assembly.
   - Move or rewire the samples per the decision above.
3. **Publish Bobcat rc.2.** Then the Wolverine PR bumps to rc.2 and merges, and WolverineFx.Bobcat ships.
4. **#745 Supervisor:** three files switch to `Bobcat.Resilience.DispositionKind`, plus the same guard test. This can
   ride in step 2 or follow it.
5. **Docs and skills** with step 2:
   - `compatibility.md`, `docs/integrations/*`, `docs/agents/*`, CLAUDE.md's CritterStack section (it becomes a
     pointer to WolverineFx.Bobcat).
   - The ai-skills authoring skill: the package for `CritterStackSpec`, and the symptom "`CritterStackSpec` not found
     → add WolverineFx.Bobcat".
6. **Follow-up PRs** dropping the `VersionOverride`s in Marten, Polecat, Weasel, Fisher, CritterWatch and Wolverine
   build projects.

## Decisions this reverses (logged in `bdd-tools-comparison.md`)

- **#63:** the recovery-hint attributes lived in JasperFx so a suite could annotate without referencing Bobcat. Nobody
  used that, and the price was a JasperFx floor in every consumer.
- **#103 and #471 Option A:** the store-agnostic helpers lived in Bobcat core, so a store-only suite needed no
  Wolverine. They stay store-agnostic, but they ship from WolverineFx.Bobcat until its rename settles their home.
- **#693:** "no split for 1.0" is superseded.

## Open questions

1. Where do the four `CritterStackSpec` samples go: **(a) the Wolverine repo** (recommended), (b) CritterStackSamples,
   or (c) stay here on the local feed?
2. Trigger enum: **`ActTrigger`**, or `TriggerKind` with the CS0104 friction?
3. Event Model publishing: **the generated bridge** (recommended), or reflection in core?
4. #745 in the #744 PR, or after it?
