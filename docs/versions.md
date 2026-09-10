# Version Matrix

The canonical, mutually-compatible dependency set for Bobcat and its samples. The `src/`
tree pins these centrally via [`src/Directory.Packages.props`](../src/Directory.Packages.props)
(Central Package Management) so the `Bobcat.*` projects can never drift apart. Samples must
target the **same** set when wired up (issue #8).

## Canonical set

| Concern | Package(s) | Version |
|---------|-----------|---------|
| Target framework | — | `net10.0` (generator is `netstandard2.0`) |
| Messaging | `WolverineFx`, `WolverineFx.RuntimeCompilation`, `WolverineFx.Marten`, `WolverineFx.Fisher`, `WolverineFx.Http`, `WolverineFx.*` | `6.35.0` |
| Document/event store (Postgres) | `Marten`, `Marten.AspNetCore` | `9.33.0` |
| Event store (SQLite, inner loop) | `Fisher` | `1.3.0` |
| Event store (SQL Server) | `Polecat` | `5.25.0` |
| Critter Stack core | `JasperFx`, `JasperFx.Events`, `JasperFx.Events.SourceGenerator` | `2.67.1` |
| HTTP testing | `Alba` | `8.5.2` |
| Test stack | `Microsoft.NET.Test.Sdk` / `xunit` / `xunit.runner.visualstudio` / `Shouldly` / `NSubstitute` / `coverlet.collector` | `18.4.0` / `2.9.3` / `3.1.5` / `4.3.0` / `5.3.0` / `3.1.2` |

## The samples now target the canonical set

The standing exception — `samples/BankAccountES` on WolverineFx 6.31.0 while `src/` stayed on
6.30.1 — is **gone**. It existed because `Wolverine.CritterWatch 1.0.2-vehicle.1` floors at
6.31.0, and following it in `src/` would have forced JasperFx above 2.56.0. JasperFx has now
moved to 2.67.1 for its own reason (below), so the whole set re-aligned above the client's floor
and the gap closed with it. That was issue **#191**.

The other samples still pin WolverineFx 6.29.1 and resolve their own stores. They are standalone
consumers with no `ProjectReference` to Bobcat, so they never see this repo's JasperFx and the
load-order rule below does not reach them. `BankAccountES` is the one sample that *does* link
against the Bobcat projects, which is exactly why it is the one that must track the set.

## Why these versions line up

The whole set is anchored by one compatibility chain:

```
WolverineFx.Marten 6.35.0  →  Marten 9.32.1+   →  JasperFx(.Events) 2.67.0  (Marten 9.33.0's floor)
WolverineFx.Fisher 6.35.0  →  Fisher 1.2.0+    →  JasperFx(.Events) 2.67.0  (Fisher 1.3.0's floor)
WolverineFx 6.35.0         →  JasperFx(.Events) 2.66.1
Polecat 5.25.0             →  JasperFx(.Events) 2.67.0
```

Every floor is at or below the pin, so taking the newest of each still lands on a single
`JasperFx.Events` (2.67.1) — the property that matters, because the event types (`IEvent`, etc.)
only unify when every package resolves the same one. Mixing (e.g. WolverineFx 5.30.x with
Marten 9.x) splits `JasperFx`/`JasperFx.Events` across major lines and they no longer unify.

### ⚠️ A floor constrains resolution, not the vtable

This is the rule the 2026-09-09 bump was taught the hard way, and it is the thing to read before
moving `JasperFx` on its own again.

JasperFx moving alone *looked* safe: every store declares it only as a floor (`>= 2.56.0`), so
2.67.1 satisfies them all and one JasperFx.Events still serves everyone. Resolution was indeed
fine. Loading was not:

```
System.TypeLoadException: Method 'QueryStreamStates' in type 'Marten.Events.QueryEventStore'
from assembly 'Marten, Version=9.30.0.0' does not have an implementation.
```

JasperFx.Events 2.67 added `QueryStreamStates` to `IReadOnlyEventStore` as an **abstract**
member. A store compiled against 2.56.0 has no slot for it, so the type fails to load the first
time anything opens a session — for Bobcat, every arrange step. It took out Marten 9.30.0 and
Fisher 1.0.4 identically: 13 failures in `Bobcat.CritterStack.Tests`, 3 in `Bobcat.Marten.Tests`,
9 in `Bobcat.CodeFirst.Samples`, and the `BankAccountES`-on-Fisher leg in the samples build.

A nuspec cannot predict this: `>= 2.56.0` means the store can run against 2.67.1 *for members
that existed when it was built*. An interface gaining an abstract member is a runtime break for
every implementer. **The only safe signal is that the store itself was built against the JasperFx
you are pinning** — which is why Marten 9.33.0, Fisher 1.3.0 and Polecat 5.25.0 are the versions
here: each is compiled against JasperFx 2.67.0, so each necessarily implements whatever 2.67 made
abstract. WolverineFx is exempt from the rule; it implements none of the event-store interfaces.

The mirror-image hazard runs the other way and is why the set moves as a **unit**. On
CritterWatch's bump, `EventQuery.TagValues` joined `EventQueryFilters.All`: a store *rebuilt*
against a newer JasperFx without implementing the filter would **claim** it and silently return
unfiltered results. Enum constants inline at compile time, so old stores were safe from that one
precisely by being old — the property this bump gives up. Watch for event queries returning too
much, not for an exception.

Two wrinkles worth knowing:

- Weasel unifies cleanly on this set: Marten 9.33.0 floors Weasel at 9.31.1, Fisher 1.3.0 at
  9.31.0, so `Weasel.Storage` resolves to 9.31.1 for both.
- Marten and Fisher each bundle `JasperFx.Events.SourceGenerator` inside their own nupkgs, so a
  project referencing both stores loads the generator twice and every projection's `Evolver`
  partial is emitted twice (CS0433 — jasperfx#462). The fix, ported from CritterWatch: a
  `DropDuplicateBundledEventSourceGenerator` target drops every store-bundled copy and the project
  references one explicit `JasperFx.Events.SourceGenerator` as an analyzer, so the generator always
  matches the runtime. `Bobcat.CritterStack.Tests` and `samples/BankAccountES` both carry it.
  Note the trap: on the previous set the two bundled copies were **byte-identical** and deduped
  themselves, so `BankAccountES` compiled without the target. Moving the stores to different
  JasperFx builds is what made them differ — a bump can therefore *introduce* CS0433 in a project
  that never had it.

### History

| Date | Set | Why |
|------|-----|-----|
| 2026-09-09 | WolverineFx 6.35.0 / Marten 9.33.0 / JasperFx 2.67.1 / Fisher 1.3.0 / Polecat 5.25.0 | CritterWatch#1212 needs partial Event Model descriptors to round-trip (jasperfx#807, in 2.67.1). JasperFx was moved alone first and broke every store at runtime — `IReadOnlyEventStore.QueryStreamStates` became abstract in 2.67 — so the whole set re-aligned onto stores built against 2.67.0. Closes the samples/src pin gap (#191). |
| 2026-08-28 | WolverineFx 6.30.1 / Marten 9.30.0 / JasperFx 2.56.0 / Fisher 1.0.4 / Polecat 5.20.0 | Issue #172: the four-source event-model vehicle needs Wolverine ≥ 6.30.1 (chains carry EM roles, `event-model` export with a push URL). JasperFx had already moved to 2.56.0 for descriptor provenance (jasperfx#703/#704). |
| 2026-08-21 | WolverineFx 6.29.1 / Marten 9.28.0 / JasperFx 2.53.0 / Fisher 1.0.2 / Polecat 5.19.2 | Issue #125: every published Fisher needs JasperFx.Events ≥ 2.47.0, and `ProjectionScenario<,>` (JasperFx.Events.TestSupport) only ships from 2.38.0. (JasperFx then moved alone to 2.54.0 for #106's descriptor, and to 2.56.0 for provenance — floors permitted the solo moves.) |
| 2026-08 | WolverineFx 6.24.2 / Marten 9.22.0 / JasperFx 2.37.0 | Recovery hints (`JasperFx.Testing`, issue #63) needed JasperFx 2.37.0. |

## What changed during reconciliation (issue #8, prerequisite)

- `Bobcat` pinned `JasperFx 2.2.3` while `Bobcat.Marten` pulled `JasperFx 2.8.2` transitively →
  unified to **2.8.2**.
- `Bobcat.Wolverine` pinned `WolverineFx 6.2.2` while the samples' `WolverineFx.Marten` needs the
  `6.5.x` line → unified the family to **6.5.1**.
- Per-project package versions across `src/` were centralized into `src/Directory.Packages.props`.

## Samples: target set for wiring (issue #8)

Each sample currently pins `WolverineFx.* 5.30.0` on `net9.0`. Wiring a sample to BobcatRunner
(see [sample-wiring.md](sample-wiring.md)) requires moving it onto the canonical set above:

1. Host `.csproj`: `TargetFramework` → `net10.0`.
2. `WolverineFx.*` package references → `6.5.1` (this is a **major upgrade** from 5.30.x —
   expect Wolverine 6 API breaking changes to fix, separate from the spec/host reconciliation).
3. `Marten` (if referenced directly) → `9.6.0`.

This version reconciliation is the prerequisite; the per-host upgrade + RESTful-contract
reconciliation + a live PostgreSQL run are the remaining per-sample work tracked in #8.
