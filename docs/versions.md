# Version Matrix

The canonical, mutually-compatible dependency set for Bobcat and its samples. The `src/`
tree pins these centrally via [`src/Directory.Packages.props`](../src/Directory.Packages.props)
(Central Package Management) so the `Bobcat.*` projects can never drift apart. Samples must
target the **same** set when wired up (issue #8).

## Canonical set

| Concern | Package(s) | Version |
|---------|-----------|---------|
| Target framework | — | `net10.0` (generator is `netstandard2.0`) |
| Messaging | `WolverineFx`, `WolverineFx.RuntimeCompilation`, `WolverineFx.Marten`, `WolverineFx.Fisher`, `WolverineFx.Http`, `WolverineFx.*` | `6.30.1` |
| Document/event store (Postgres) | `Marten`, `Marten.AspNetCore` | `9.30.0` |
| Event store (SQLite, inner loop) | `Fisher` | `1.0.4` |
| Event store (SQL Server) | `Polecat` | `5.20.0` |
| Critter Stack core | `JasperFx`, `JasperFx.Events`, `JasperFx.Events.SourceGenerator` | `2.56.0` |
| HTTP testing | `Alba` | `8.5.2` |
| Test stack | `Microsoft.NET.Test.Sdk` / `xunit` / `xunit.runner.visualstudio` / `Shouldly` / `NSubstitute` / `coverlet.collector` | `18.4.0` / `2.9.3` / `3.1.5` / `4.3.0` / `5.3.0` / `3.1.2` |

## One deliberate exception: `samples/BankAccountES` on WolverineFx 6.31.0

`samples/BankAccountES` pins **WolverineFx 6.31.0**, one release above the canonical set, because
`Wolverine.CritterWatch 1.0.2-vehicle.1` — the client the #172 fourth-rung vehicle needs — floors
there. `src/` deliberately did **not** follow, and the reason is the same trap
`Directory.Packages.props` already records for 6.30.2+:

```
WolverineFx 6.31.0  →  JasperFx / JasperFx.Events / JasperFx.SourceGenerator 2.57.2
```

That is above this repo's 2.56.0, so taking it means re-aligning the whole set — every store has
to resolve one `JasperFx.Events` or the event types stop unifying — and re-checking the
duplicate-bundled-source-generator workaround against the new store packages. A #125-class bump
with its own verification, tracked as **issue #191**, not something to fold into a release.

The exception is safe because the sample is a *consumer* of the Bobcat packages, not part of the
shipped set: it resolves its own Wolverine and Bobcat's libraries do not care which one it got. It
is recorded here rather than left silent because "samples target the canonical set" (issue #8) is
otherwise a claim this repo would be quietly breaking.

## Why these versions line up

The whole set is anchored by one compatibility chain:

```
WolverineFx.Marten 6.30.1  →  Marten 9.23.0+   →  JasperFx(.Events) 2.56.0  (Marten 9.30.0's floor)
WolverineFx.Fisher 6.30.1  →  Fisher 1.0.2+    →  JasperFx(.Events) 2.56.0  (Fisher 1.0.4's floor)
WolverineFx 6.30.1         →  JasperFx(.Events) 2.56.0
Polecat 5.20.0             →  JasperFx(.Events) 2.56.0
```

So the entire `WolverineFx.*` family must be **6.30.1**, Marten at least **9.23.0** and
`JasperFx` exactly **2.56.0** (every current member floors at 2.56.0) for a single,
conflict-free `JasperFx` to satisfy everything. Mixing (e.g. WolverineFx 5.30.x with Marten 9.x)
splits `JasperFx`/`JasperFx.Events` across major lines and the event types (`IEvent`, etc.) no
longer unify.

⚠️ **WolverineFx 6.30.2 and 6.30.3 raise the JasperFx floor to 2.57.1** — taking either means
moving the whole set's JasperFx, which is why the pin sits at 6.30.1 (also CritterWatch's proven
pin). Check that property again on the next bump rather than assuming newest-of-each always
preserves it (the nuspec `dependencies` groups on `api.nuget.org/v3-flatcontainer/<id>/<version>/<id>.nuspec`
are the source of truth; the check that produced this table is recorded in
`src/Directory.Packages.props`).

Two wrinkles worth knowing:

- Weasel unifies cleanly on this set: Marten 9.30.0 and Fisher 1.0.4 both floor their Weasel
  packages at 9.27.0 (the earlier 9.24.0/9.25.1 skew is gone).
- Marten 9.30.0 and Fisher 1.0.4 bundle the **byte-identical** `JasperFx.Events.SourceGenerator`
  inside their own nupkgs, so a project referencing both stores loads the generator twice and
  every projection's `Evolver` partial is emitted twice (CS0433 — jasperfx#462). The fix, ported
  from CritterWatch: a `DropDuplicateBundledEventSourceGenerator` target drops every store-bundled
  copy and the project references the centrally-pinned `JasperFx.Events.SourceGenerator` as an
  explicit analyzer, so the generator always matches the runtime. `Bobcat.CritterStack.Tests`
  carries the pattern.

### History

| Date | Set | Why |
|------|-----|-----|
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
