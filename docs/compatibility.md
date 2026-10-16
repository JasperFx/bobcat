# Version Compatibility

Which Bobcat works with which .NET, test framework, Marten, Polecat, Fisher and Wolverine.

## The one rule

Bobcat references **no event store and no Wolverine**. Its only Critter Stack dependency is
`JasperFx` and `JasperFx.Events`, declared as a minimum version. Marten, Polecat, Fisher and
Wolverine all depend on those two packages as well, so in a spec project they meet in a single
dependency graph, and **every package must resolve the same `JasperFx.Events`**. That is what lets
an `IEvent` from Marten be the `IEvent` Bobcat reads.

In practice:

- Take a Marten, Polecat, Fisher or Wolverine release **built against the JasperFx.Events that
  Bobcat requires or newer** (the table below). NuGet resolves the highest minimum anyone asks for,
  so an older store next to a newer Bobcat restores fine and can still fail at run time. A store
  built against an older JasperFx.Events can hit a `TypeLoadException` (`Method '…' in type '…'
  does not have an implementation`) the first time it opens a session.
- If you see that exception, or `IEvent` and other event types that "should" match don't, move the
  stores and Wolverine up to releases built on the JasperFx.Events your graph resolves. Don't pin
  JasperFx below what Bobcat asks for.

## Bobcat 1.0

| | Requirement | Built and tested against |
|---|---|---|
| .NET | `net9.0` or `net10.0` for the libraries; the `bobcat` tool needs the .NET 10 SDK | .NET 10 |
| `JasperFx`, `JasperFx.Events` | 2.90.2 or later | 2.90.2 |
| Marten | a release built on JasperFx.Events 2.80 or later | 9.46.0 |
| Fisher | a release built on JasperFx.Events 2.80 or later | 1.19.0 |
| Polecat | a release built on JasperFx.Events 2.80 or later | 5.36.0 (not run in CI; it needs SQL Server) |
| WolverineFx | through `WolverineFx.Bobcat`, below | 6.46.0 |
| Alba | any; Bobcat does not reference it | 8.5.2 |

The "built and tested against" column is what this repository's own suites and samples run on,
against real Postgres (Marten) and SQLite (Fisher).

### Test frameworks

Each runner adapter depends on its framework at the version below as a minimum.

| Package | Framework | Notes |
|---|---|---|
| `Bobcat.Xunit` | `xunit.v3` 4.x on Microsoft.Testing.Platform 2.x (2.5.1 or later) | The project must run on Microsoft.Testing.Platform, not VSTest: set the test runner in `global.json` as [Getting Started](/getting-started) shows |
| `Bobcat.TUnit` | `TUnit.Core` 1.74.0 or later | Takes no platform reference; your `TUnit` package brings the platform |
| `Bobcat.NUnit` | `NUnit` 4.3.2 or later | Tested on NUnit3TestAdapter 5.2 |
| `Bobcat.MSTest` | MSTest 3.10 or later (global test hooks) | References no MSTest assembly, so it works on MSTest 3 and 4; tested on MSTest 3.11 here and 4.5 as a package consumer |

**Bobcat 0.60.0 moved to xUnit v3 4.x.** Earlier releases used `xunit.v3` 3.x on
Microsoft.Testing.Platform 1.9. The two don't mix in one test project: if you upgrade Bobcat from a
release before 0.60.0, upgrade `xunit.v3` to 4.x in the same change.

## WolverineFx.Bobcat

`WolverineScenario`, `WolverineSpec` and `WolverineHttpSpec` ship from the Wolverine repository as
**`WolverineFx.Bobcat`**, versioned with Wolverine. Each release names the Bobcat it was built
against as its minimum:

| WolverineFx.Bobcat | Requires Bobcat | Requires WolverineFx |
|---|---|---|
| 6.50.0-alpha.1 and later | 0.60.4 or later | the same version |
| 6.49.0-alpha.3 | 0.38.0 or later | 6.49.0 |
| 6.48.x | 0.32.0 or later | the same version |

Use a `WolverineFx.Bobcat` whose minimum Bobcat is the release you are on, or the closest one
below it. A `WolverineFx.Bobcat` built against an older Bobcat may compile against a newer one but
is not tested with it. `bobcat import-event-model --specs` writes the version into the spec
project it generates. Pass `--wolverine-version` to choose a different one (see
[the `bobcat` tool](/bobcat-tool)).

## Earlier Bobcat releases

The JasperFx minimum is the line that matters when you upgrade.

| Bobcat | `JasperFx` / `JasperFx.Events` minimum | Test framework line |
|---|---|---|
| 1.0 | 2.90.2 | xUnit v3 4.x, MTP 2.x |
| 0.60.x | 2.85.0 | xUnit v3 4.x, MTP 2.x |
| 0.39.0 – 0.50.0 | 2.84.1 | xUnit v3 3.x, MTP 1.9 |
| 0.37.0 – 0.38.1 | 2.82.0 | xUnit v3 3.x, MTP 1.9 |
| 0.31.0 – 0.36.1 | 2.81.0 | xUnit v3 3.x, MTP 1.9 |
| 0.21.0 – 0.30.0 | 2.69.3 | xUnit v3 3.x, MTP 1.9 |
| 0.18.0 – 0.20.0 | 2.67.1 | xUnit v3 3.x, MTP 1.9 |

Releases before 0.50.0 also carry the retired Gherkin lane. Bobcat 0.50.0 removed it (see
[Diagnostics](/diagnostics#retired) for the diagnostics that went with it).
