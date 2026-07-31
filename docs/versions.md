# Version Matrix

The canonical, mutually-compatible dependency set for Bobcat and its samples. The `src/`
tree pins these centrally via [`src/Directory.Packages.props`](../src/Directory.Packages.props)
(Central Package Management) so the `Bobcat.*` projects can never drift apart. Samples must
target the **same** set when wired up (issue #8).

## Canonical set

| Concern | Package(s) | Version |
|---------|-----------|---------|
| Target framework | — | `net10.0` (generator is `netstandard2.0`) |
| Messaging | `WolverineFx`, `WolverineFx.RuntimeCompilation`, `WolverineFx.Marten`, `WolverineFx.Http`, `WolverineFx.*` | `6.24.2` |
| Document/event store | `Marten` | `9.22.0` |
| Critter Stack core | `JasperFx`, `JasperFx.Events` | `2.36.3` |
| HTTP testing | `Alba` | `8.5.2` |
| Test stack | `Microsoft.NET.Test.Sdk` / `xunit` / `xunit.runner.visualstudio` / `Shouldly` / `NSubstitute` / `coverlet.collector` | `18.4.0` / `2.9.3` / `3.1.5` / `4.3.0` / `5.3.0` / `3.1.2` |

## Why these versions line up

The whole set is anchored by one compatibility chain:

```
WolverineFx.Marten 6.24.2  →  Marten 9.20.0+  →  JasperFx(.Events) 2.36.1+
```

So the entire `WolverineFx.*` family must be **6.24.2** and Marten at least **9.20.0** for a
single, conflict-free `JasperFx` to satisfy everything. Mixing (e.g. WolverineFx 5.30.x
with Marten 9.x) splits `JasperFx`/`JasperFx.Events` across major lines and the event types
(`IEvent`, etc.) no longer unify.

The pins above take the newest release of each rather than the exact floor Wolverine declares —
Marten **9.22.0** over the required 9.20.0, and `JasperFx` **2.36.3** over 2.36.1. That is safe
here precisely because Marten 9.22.0 itself requires JasperFx 2.36.3: the chain still terminates
in one JasperFx version, which is the invariant this matrix exists to protect. Check that
property again on the next bump rather than assuming newest-of-each always preserves it.

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
