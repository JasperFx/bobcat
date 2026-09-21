# Version Matrix

The canonical, mutually-compatible dependency set for Bobcat and its samples. The `src/`
tree pins these centrally via [`src/Directory.Packages.props`](../src/Directory.Packages.props)
(Central Package Management) so the `Bobcat.*` projects can never drift apart. Samples must
target the **same** set when wired up (issue #8).

## Canonical set

| Concern | Package(s) | Version |
|---------|-----------|---------|
| Target framework | — | `net10.0` (generator is `netstandard2.0`) |
| Messaging | `WolverineFx`, `WolverineFx.RuntimeCompilation`, `WolverineFx.Marten`, `WolverineFx.Fisher`, `WolverineFx.Http`, `WolverineFx.*` | `6.38.0` |
| Document/event store (Postgres) | `Marten` | `9.36.0` |
| Event store (SQLite, inner loop) | `Fisher` | `1.10.0` |
| Event store (SQL Server) | `Polecat` | `5.29.0` |
| Critter Stack core | `JasperFx`, `JasperFx.Events`, `JasperFx.Events.SourceGenerator` | `2.69.3` |
| HTTP testing | `Alba` | `8.5.2` |
| Test stack | `xunit.v3` / `Microsoft.Testing.Platform` / `Shouldly` / `NSubstitute` | `3.2.2` / `1.9.1` / `4.3.0` / `5.3.0` |
| Second runner (adapter surface) | `TUnit.Core` | `1.66.27` |

## Which samples track the canonical set, and which deliberately do not

The standing exception — `samples/BankAccountES` on WolverineFx 6.31.0 while `src/` stayed on
6.30.1 — is **gone**. It existed because `Wolverine.CritterWatch 1.0.2-vehicle.1` floors at
6.31.0, and following it in `src/` would have forced JasperFx above 2.56.0. JasperFx has now
moved to 2.67.1 for its own reason (below), so the whole set re-aligned above the client's floor
and the gap closed with it. That was issue **#191**, against the 6.35.0/2.67.1 set; the canonical
set has moved on twice since and the exception has not come back.

**Every sample now tracks the set**, pinned centrally in
[`samples/Directory.Packages.props`](../samples/Directory.Packages.props) since 2026-09-21.

That paragraph used to say the other samples were standalone consumers with no `ProjectReference`
to Bobcat, so they never saw this repo's JasperFx and the load-order rule below did not reach
them. **That was wrong on both counts**, and it is why half the samples sat on WolverineFx 6.29.1
/ Marten 9.28.0 unexamined. Every sample `Tests` project does project-reference `src/Bobcat`, so
every one of them resolves this repo's JasperFx.Events — and Marten 9.28.0, built before 2.67 made
`QueryStreamStates` abstract, therefore died on the exact TypeLoadException described below the
first time it opened a session. Nothing reported it because `samples.yml` only *builds* the
samples; running `CqrsMinimalApi` is what surfaced it.

## Why these versions line up

The whole set is anchored by one compatibility chain:

```
WolverineFx.Marten 6.38.0  →  Marten 9.35.0+   →  JasperFx(.Events) 2.69.3  (Marten 9.36.0's floor)
WolverineFx.Fisher 6.38.0  →  Fisher 1.10.0+   →  JasperFx(.Events) 2.69.3  (Fisher 1.10.0's floor)
WolverineFx 6.38.0         →  JasperFx(.Events) 2.69.3
Polecat 5.29.0             →  JasperFx(.Events) 2.69.3
```

Every floor is at or below the pin, so taking the newest of each still lands on a single
`JasperFx.Events` — the property that matters, because the event types (`IEvent`, etc.) only unify
when every package resolves the same one. Mixing (e.g. WolverineFx 5.30.x with Marten 9.x) splits
`JasperFx`/`JasperFx.Events` across major lines and they no longer unify.

As of this set the alignment is **exact rather than merely compatible**: WolverineFx, Marten, Fisher
and Polecat all floor at `JasperFx(.Events) 2.69.3`, which is also the pin. There is no headroom
between any floor and the pinned version, so nothing in the set can be moved on its own without
first moving JasperFx — which is the safer arrangement, given the vtable rule below is what a floor
cannot express.

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
you are pinning** — which is why Marten 9.36.0, Fisher 1.10.0 and Polecat 5.29.0 are the versions
here: each declares JasperFx(.Events) at *exactly* 2.69.3, so each was built against the runtime it
will load. WolverineFx used to be exempt from the rule, implementing none of the event-store
interfaces; on this set it declares 2.69.3 itself and needs no exemption.

The mirror-image hazard runs the other way and is why the set moves as a **unit**. On
CritterWatch's bump, `EventQuery.TagValues` joined `EventQueryFilters.All`: a store *rebuilt*
against a newer JasperFx without implementing the filter would **claim** it and silently return
unfiltered results. Enum constants inline at compile time, so old stores were safe from that one
precisely by being old — the property this bump gives up. Watch for event queries returning too
much, not for an exception.

Two wrinkles worth knowing:

- Weasel unifies cleanly on this set: Marten 9.36.0, Fisher 1.10.0 and Polecat 5.29.0 each floor
  their Weasel provider at 9.32.0, and `Weasel.Storage` resolves to 9.32.0 for all of them.
- Marten and Fisher each bundle `JasperFx.Events.SourceGenerator` inside their own nupkgs, so a
  project referencing both stores loads the generator twice and every projection's `Evolver`
  partial is emitted twice (CS0433 — jasperfx#462). The fix, ported from CritterWatch: a
  `DropDuplicateBundledEventSourceGenerator` target drops every store-bundled copy and the project
  references one explicit `JasperFx.Events.SourceGenerator` as an analyzer, so the generator always
  matches the runtime. `Bobcat.CritterStack.Tests` and `samples/BankAccountES` both carry it.
  Note the trap: whether the two bundled copies collide depends on the set. On this one they are
  **byte-identical again** (both SHA-256 `5d15fdf9…`), so they dedupe themselves and the target is
  belt-and-braces today. That is not an all-clear — a project without it compiles fine now and
  starts failing CS0433 the moment a bump puts the two stores on different JasperFx builds.

### History

| Date | Set | Why |
|------|-----|-----|
| current | WolverineFx 6.38.0 / Marten 9.36.0 / JasperFx 2.69.3 / Fisher 1.10.0 / Polecat 5.29.0 | **WolverineFx 6.38.0 is the recurring-schedule fixes.** GH-4436 (`c1c726469`, PR #4444) — a non-UTC recurring schedule recorded no tracking row, because Cronos hands back each occurrence carrying the *schedule's* offset and Npgsql's `timestamptz` binder refuses any `DateTimeOffset` whose offset is not zero; `RecurringMessageRecord` now normalizes in its `init` accessors, the way `Envelope.ScheduledTime` already did in its setter. GH-4437 (`2064a66e7`, PR #4451) — occurrence attribution and a manual trigger. The stores and JasperFx followed to keep the set coherent under it. Re-verified against the nuspecs on 2026-09-21: every store floors at exactly 2.69.3, a tighter guarantee than the set it replaced. |
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
(see [sample-wiring-playbook.md](sample-wiring-playbook.md)) requires moving it onto the canonical set above:

1. Host `.csproj`: `TargetFramework` → `net10.0`.
2. `WolverineFx.*` package references → `6.5.1` (this is a **major upgrade** from 5.30.x —
   expect Wolverine 6 API breaking changes to fix, separate from the spec/host reconciliation).
3. `Marten` (if referenced directly) → `9.6.0`.

This version reconciliation is the prerequisite; the per-host upgrade + RESTful-contract
reconciliation + a live PostgreSQL run are the remaining per-sample work tracked in #8.
