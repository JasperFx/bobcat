# Decoupling Bobcat from JasperFx (#744, #745)

Status: **design note for review**, 2026-10-18. Decided by Jeremy the same day: `Bobcat` and `Bobcat.Supervisor` take
no dependency on JasperFx or JasperFx.Events. Breaking changes are fine before 1.0. This reverses #63 and #103 (see
"Decisions this reverses" below).

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
| Critter Stack vocabulary | `CritterStack/` `CritterStackScenario`, `CritterStackSpec`, `CritterStackActOutcome`, `EventStores`, `DocumentStores`, `StoreRouting`, `EventStoreAuthoring`, `CritterStackHostExtensions` | `JasperFx.Events` (`.Tags`, `.Daemon`, `.Projections`, `.Documents`, `.Descriptors`), `JasperFx.Resources`, `JasperFx.CommandLine.Descriptions` | **new package `Bobcat.CritterStack`** |
| Store-free helpers in the same folder | `RecordBuilding`, `ProjectionsBehind`, `ActTimedOutException`, `ProjectionCatchUpTimeoutException` | none | **stay in core**. `TableRunner` calls `RecordBuilding`. |
| Event Model reading | `EventModel/GeneratedEventModel`, `EventModel/SpecIdentityAudit`, `SpecificationBindings` (already `[Obsolete]`) | `EventModelDescriptor`, `IEventModelDefinitionSource`, `SpecificationManifest` | `GeneratedEventModel` and `SpecIdentityAudit` move to **`Bobcat.CritterStack`**. `SpecificationBindings` and Bobcat's old `SpecificationManifestAttribute` are **deleted** (they are obsolete already, #528). |
| Event Model publishing | `Monitoring/SpecEventModelPublisher`, called from `MarkerStepRun` on attach | `EventModelDescriptor` (serialized to JSON) | **stays in core**, but it is handed JSON instead of a descriptor (see "The publishing seam" below) |
| Vocabulary role attributes | `EventModel/EventModelRoleAttribute.cs` (`EventModelRoleAttribute`, `EventModelTriggerAttribute`, `SliceRole`) | `TriggerKind` only. `SliceRole` is already Bobcat's own type. | **stay in core**, with a Bobcat-owned trigger enum (see "Role attributes" below) |
| Resilience | `Resilience/Disposition`, `DefaultFailurePolicy`, `HintedFailurePolicy`, `RecoveryHint`, `Runtime/SpecManifestHint` | `JasperFx.Testing.DispositionKind` and the four recovery-hint attributes | **Bobcat owns both in core**, namespace `Bobcat.Resilience` |
| Preflight | `Runtime/Preflight` | `EnvironmentCheckResults`, `EnvironmentChecker` | Bobcat result type in core. `AddContainerChecks` moves to **`Bobcat.CritterStack`**. |
| Rendering | `CommandLineRenderer`, `RenderingClasses` | `JasperFx.Core` extension helpers | inline the few calls |

The generator needs no change for any of this:

- It reads `[EventModelRole]`/`[EventModelTrigger]` by metadata name and the trigger by enum member name.
- It emits `global::JasperFx.Events.EventModeling.*` only behind its type probe on the consumer's compilation.
- A spec assembly that does no event sourcing still gets no Event Model source, exactly as today.

## The shape

```
Bobcat                 (no JasperFx)   projected engine, verification, partial objects, rendering, monitor wire,
                                       resident runner, Resilience types, Preflight, role attributes
Bobcat.CritterStack    → Bobcat, JasperFx.Events
                                       CritterStackScenario/Spec, EventStores, routing, GeneratedEventModel,
                                       SpecIdentityAudit, Preflight.AddContainerChecks, ClearStatefulResourcesAsync
Bobcat.Supervisor      → Bobcat        (no JasperFx, so #745 falls out of #744)
Bobcat.Xunit/TUnit/NUnit/MSTest → Bobcat   (unchanged, already JasperFx-free)
Bobcat.EventModel      → JasperFx.Events   (unchanged: the importer's library, used by the tool, not by spec projects)
Bobcat.Console, Bobcat.Ui              tools: keep JasperFx.CommandLine. A global tool's graph doesn't reach a consumer's restore.
WolverineFx.Bobcat     → Bobcat.CritterStack   (was → Bobcat)
```

- **The namespace stays `Bobcat.CritterStack`**, so a spec project only gains a package reference and no source
  changes. A WolverineFx.Bobcat user gets it transitively and changes nothing.
- **A tenth package.** The Nuke `Pack` target, `publish.yml` and `docs/compatibility.md` gain `Bobcat.CritterStack`.

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
  #294 requires, using the name it was given rather than one read off a descriptor.
- The serialization moves into the consumer's compilation, which by construction references JasperFx.Events. The
  generated payload's JSON shape must match today's byte for byte. A test pins today's output, then the new path
  against it.
- The alternative is reflection over `IEventModelDefinitionSource` by name inside core. It keeps the generator
  unchanged, but it means core reflects over a JasperFx type and depends on JasperFx's serialization shape without a
  reference. I prefer the generated bridge: the type probe already exists, and "the consumer's compilation decides"
  is the rule the generator already follows.
- `SpecIdentityAudit` (#338) needs the real descriptor, so it moves with `GeneratedEventModel` to
  `Bobcat.CritterStack`. A suite that runs the audit already does event sourcing.

## Role attributes: a Bobcat-owned trigger enum

`EventModelTriggerAttribute(TriggerKind kind)` takes JasperFx's enum. Core gets its own enum with the same member
names, because the generator matches member names (`"Http"`), so the emitted descriptor is unchanged.

**Naming question.** The obvious name, `Bobcat.TriggerKind`, is ambiguous (CS0104) in any vocabulary file that also
imports `JasperFx.Events.EventModeling`, and WolverineFx.Bobcat's does. My recommendation is a distinct name,
**`ActTrigger`** (`[EventModelTrigger(ActTrigger.Http, Route = nameof(route))]`). It is also more accurate: it names the
act's trigger, which upstream derives and never lets anyone declare.

## Resilience: Bobcat owns the vocabulary, JasperFx.Testing's copy retires

`DispositionKind` and `[ClearsOnRetry]` / `[ClearsOnRecycle]` / `[ClearsInFreshProcess]` / `[NeverRecovers]` move to
`Bobcat.Resilience` (attributes in core, so the worker reads them as it does today, #469).

- **No adapter for the JasperFx copies.** I checked wolverine, marten, polecat, fisher, weasel and CritterWatch: nothing
  outside JasperFx's own tests annotates with them. Reading both would keep two vocabularies alive for nobody.
- **`ResourceParsingAgreementTests` goes**: it pinned two parsers of one resource list across the repo boundary, and
  there is now one.
- **JasperFx follow-up:** mark `JasperFx.Testing`'s types `[Obsolete]`, pointing at Bobcat (a jasperfx issue; the lead
  files it).
- The monitor wire carries the disposition as a **string**, so the wire doesn't change. `Bobcat.Ui`'s contract copy
  switches enums.

## Preflight

`Preflight.Run` returns a Bobcat `PreflightResults` (successes, failures, `Succeeded`, `Assert()`), shaped like
`EnvironmentCheckResults` so call sites change only the type name.

- `ITestResource.Check` keeps mirroring `IStatefulResource.Check` by **shape**, not by type, so one resource still
  satisfies both.
- `AddContainerChecks(IServiceProvider)`, which delegates to `EnvironmentChecker`, becomes an extension method in
  `Bobcat.CritterStack`.
- The CLAUDE.md rule "there is no Bobcat `IEnvironmentCheck`" still holds. We add a result type, not a check
  abstraction.

## Breaking changes (for the rc.2 migration notes)

1. A spec project that uses `CritterStackSpec`/`CritterStackScenario`, `EventStores`, `SpecIdentityAudit` or
   `AddContainerChecks` adds `<PackageReference Include="Bobcat.CritterStack" />`. WolverineFx.Bobcat users get it
   transitively.
2. `JasperFx.Testing.DispositionKind` and the hint attributes become `Bobcat.Resilience.*`. In practice that means
   `using` changes in custom `IFailurePolicy`s.
3. `Preflight.Run` returns `PreflightResults`.
4. `[EventModelTrigger(TriggerKind.Http, …)]` becomes `ActTrigger.Http`. This affects WolverineFx.Bobcat's vocabulary
   and nothing in a spec.
5. `SpecificationBindings` and Bobcat's `SpecificationManifestAttribute` are deleted (obsolete since #528).
6. A suite that got JasperFx **transitively through Bobcat** and uses it directly now needs its own reference. That is
   the point of the change; the migration notes should say so plainly.

## Sequencing

1. **#744 core PR** (one PR, since the pieces don't build apart):
   - Create `Bobcat.CritterStack` and move the eight files plus `GeneratedEventModel` and `SpecIdentityAudit`.
   - Add the Resilience, Preflight and trigger-enum types.
   - Add the publishing bridge in the generator.
   - Repoint the tests: `Bobcat.CritterStackScenario.Tests` references the new package; the samples go through
     `samples/Directory.Packages.props`.
   - A guard test asserts that `Bobcat.dll`'s references contain no `JasperFx*` assembly. Without it, the next
     convenience `using` brings the dependency back.
2. **#745 Supervisor** follows almost by construction: three files switch to `Bobcat.Resilience.DispositionKind`, and a
   guard test does the same for `Bobcat.Supervisor.dll`. It could be in the same PR. I'd keep it separate so the plan
   node closes on its own evidence.
3. **Docs and skills** in the same piece of work: `compatibility.md`, `docs/integrations/*`, `docs/agents/*`, the
   `ai-skills` authoring skill (the package list, and a new symptom: `CritterStackSpec` not found → add
   `Bobcat.CritterStack`).
4. **rc.2.** Then the companion **wolverine issue**: WolverineFx.Bobcat references `Bobcat.CritterStack` and switches to
   `ActTrigger`.
5. **Follow-up PRs** dropping the `VersionOverride`s in Marten, Polecat, Weasel, Fisher, CritterWatch and Wolverine
   build projects.

## Decisions this reverses (logged in `bdd-tools-comparison.md`)

- **#63:** the recovery-hint attributes lived in JasperFx so a suite could annotate without referencing Bobcat. Nobody
  used that, and the price was a JasperFx floor in every consumer.
- **#103:** the Critter Stack helpers lived in core. They stay store-agnostic and keep their namespace, but they now
  live in their own package.
- **#693:** "no split for 1.0" is superseded. The split is what makes the Supervisor light.

## Questions for Jeremy

1. Package name: **`Bobcat.CritterStack`** (matches the namespace), or something else?
2. Trigger enum: **`ActTrigger`**, or `TriggerKind` accepting the CS0104 friction?
3. Event Model publishing: **the generated bridge** (my recommendation) or reflection in core?
4. #745 in the same PR as #744, or separate (my recommendation: separate)?
