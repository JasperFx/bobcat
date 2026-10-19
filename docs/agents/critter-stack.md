# The Critter Stack vocabulary lives in WolverineFx.Bobcat since #744

**Bobcat core takes no JasperFx or Wolverine dependency** (`design/jasperfx-decoupling.md`, decided
2026-10-18). `BobcatAssemblyReferenceTests` fails the build if `Bobcat.dll` references a `JasperFx*` or
`Wolverine*` assembly. Do not add anything to `src/Bobcat` that needs either.

**What moved to the Wolverine repo** (`src/Bobcat/Wolverine.Bobcat`, package **WolverineFx.Bobcat**), under the
same namespaces, so a spec that already references WolverineFx.Bobcat changes nothing:

- `CritterStackScenario` / `CritterStackSpec` / `CritterStackActOutcome`, `EventStores`, `DocumentStores`,
  store routing (`In<TStore>()`, the main-store default), `CritterStackHostExtensions` (`services.EventStore()`),
  `ProjectionsBehind`, `ActTimedOutException`, `ProjectionCatchUpTimeoutException`.
- `GeneratedEventModel` and `SpecIdentityAudit` (#338), which need the real descriptor.
- `Preflight.AddContainerChecks` (JasperFx's `EnvironmentChecker`).
- `Bobcat.CritterStackScenario.Tests` (with `[PostgresFact]`'s never-skip-on-CI rule), all of `samples/`, the
  Nuke `Samples` targets and `samples.yml`, and `import-event-model --specs` (the spec writer and the spec project
  writer).

The design history of those helpers (#103, #471 Option A, #497, #503, #504) is in git before #744 and in the
Wolverine repo.

**What stayed in core:**

- `Bobcat.CritterStack.RecordBuilding` — `TableRunner` calls it; it is store-free. Its namespace is unchanged.
- The vocabulary role attributes (`[EventModelRole]`, `[EventModelTrigger]`, `[EventModelAct]`, `SliceRole`) and
  `ActTrigger`, which replaced JasperFx's `TriggerKind` on `[EventModelTrigger]` with the same member names. The
  generator reads the member by name and stamps JasperFx's member of that name, so the descriptor is unchanged.
- `Bobcat.Resilience.DispositionKind` and the recovery-hint attributes, and `PreflightResults` — see
  `resilience-and-supervisor.md`.
- Publishing the spec half of the Event Model, through the generated bridge — see `monitor-wire.md`.

**The generator still recognises `Bobcat.CritterStack.CritterStackScenario` / `CritterStackSpec` by metadata
name** (`VocabularyCalls`), with no reference — the same standing as its Wolverine.Http and MVC endpoint
attribute recognition (#500). Its tests compile a stand-in (`CritterStackStandIn`) into an assembly of its own.
