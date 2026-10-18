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
| Alba | any for Bobcat itself, which does not reference it. `WolverineFx.Bobcat.Http` 6.51.0-alpha.1 requires **9.0.0-rc.2** (below) | 8.5.2 |

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

## Adopting Bobcat in one project of a larger repository {#partial-adoption}

A large repository usually can't move every test project to xUnit v3 4.x at once. You can adopt
`Bobcat.Xunit` in one project and leave the rest on xUnit v3 3.x. Marten did this: its
`DaemonTests` takes Bobcat 1.0 and its other 24 test projects stay on `xunit.v3` 3.2.2
(JasperFx/marten#5690). Three things need care.

### Move only the Bobcat projects to xUnit v3 4.x

With Central Package Management, keep the repository's `xunit.v3` pin as it is and override it in
the projects that reference `Bobcat.Xunit`:

```xml
<!-- Directory.Packages.props -->
<PropertyGroup>
  <BobcatXunitV3Version>4.0.2</BobcatXunitV3Version>
  <BobcatXunitRunnerVersion>4.0.1</BobcatXunitRunnerVersion>
</PropertyGroup>
```

```xml
<!-- the spec project's .csproj -->
<PackageReference Include="xunit.v3" VersionOverride="$(BobcatXunitV3Version)" />
<PackageReference Include="xunit.runner.visualstudio" VersionOverride="$(BobcatXunitRunnerVersion)">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

Then **check every `ProjectReference` into the spec project.** A test project that references it
gets xUnit v3 4.x through that reference. That project then has two xUnit lines in one graph, and
3.x and 4.x can't share a test project. In Marten, `CoreTests` referenced `DaemonTests` only for a
transitive package. The fix was to remove the reference and add that package directly. A
non-test project that references the spec project (a shared test-support library, say) also needs
`IsTestingPlatformApplication=false` (see [Moving to xUnit v3 4.x](/xunit#moving-to-xunit-4)).

The rest of the [xUnit 4 move](/xunit#moving-to-xunit-4) applies to the spec project only. For
example, `[assembly: CollectionBehavior(DisableTestParallelization = true)]` is a compile error
there, and the other projects keep theirs.

### Run the spec project directly or through the supervisor, not with `dotnet test`

On the .NET 10 SDK, `dotnet test` runs an xUnit v3 4.x project only when `global.json` selects
Microsoft.Testing.Platform:

```json
{
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

That setting covers **every project under that `global.json`**, so it moves the whole repository
off VSTest. In a partial adoption you will usually leave it out. Without it, `dotnet test` on the
spec project fails, so run the spec project another way:

- **Run the built test host.** A Microsoft.Testing.Platform test project is an executable:
  `./src/MySpecs/bin/Release/net10.0/MySpecs --filter-class "*Wallet*"`.
- **Run it through `Bobcat.Supervisor`**, for example from a Nuke target. The supervisor launches
  the built host itself, so it doesn't depend on `dotnet test` at all. Marten's CI runs
  `DaemonTests` this way.

If CI runs `dotnet test` over a solution, make sure the spec project isn't part of that run.

### A build tool that references `Bobcat.Supervisor` gets Bobcat's JasperFx minimum {#build-tool-jasperfx}

`Bobcat.Supervisor` depends on `Bobcat`, and `Bobcat` requires `JasperFx` and `JasperFx.Events` at
the minimum in [the table above](#bobcat-1-0). That minimum applies to the build project too, even
though the supervisor only uses the retry and isolation types. If the build pins `JasperFx` below
that minimum, restore fails with **NU1605** (a detected package downgrade).

The build ships nothing, so raise its pin there and leave the product's own JasperFx dependency
alone:

```xml
<!-- build/build.csproj -->
<PackageReference Include="Bobcat.Supervisor" />
<PackageReference Include="JasperFx" VersionOverride="2.90.2" />
```

Don't silence NU1605 with `NoWarn`. That runs Bobcat on a JasperFx older than it was built against,
which is what the table above warns about.

## WolverineFx.Bobcat

`WolverineScenario`, `WolverineSpec` and `WolverineHttpSpec` ship from the Wolverine repository as
**`WolverineFx.Bobcat`**, versioned with Wolverine. Each release names the Bobcat it was built
against as its minimum:

| WolverineFx.Bobcat | Requires Bobcat | Requires WolverineFx |
|---|---|---|
| 6.50.0-alpha.1 – 6.51.0-alpha.1 | 0.60.4 or later | the same version |
| 6.49.0-alpha.3 | 0.38.0 or later | 6.49.0 |
| 6.48.x | 0.32.0 or later | the same version |

**No `WolverineFx.Bobcat` is built against Bobcat 1.0 yet.** For Bobcat 1.0.0-alpha.1, use
**WolverineFx.Bobcat 6.51.0-alpha.1**: it is the supported pairing until a release built against
1.0 ships, and this table gains its row then. Reference `Bobcat`, `Bobcat.Generators` and your
runner adapter at 1.0.0-alpha.1 directly. NuGet takes the higher Bobcat without a warning (6.51.0
only sets a minimum, so there is no NU1608). This pairing has run in
[CritterStackSamples](https://github.com/JasperFx/CritterStackSamples): 12 spec projects using
`WolverineSpec` (`WhenReceived`, `WhenTracked`, `ThenMessageSent`, `ThenNoMessageSent`,
`GivenReadModel`, `ThenDocument`, `ThenSingleDocument`, `ThenEvents`, `ThenStreamIsStarted`)
compiled and passed on net9.0 and net10.0.

**`WolverineFx.Bobcat.Http` 6.51.0-alpha.1 depends on Alba 9.0.0-rc.2** (bobcat#675). A test project
that references Alba 8.x gets NU1605 (a package downgrade) and has to move to the prerelease Alba
to take it, which also drops Alba 8's `ConfigurationOverride`. If you have to stay on Alba 8.x,
leave `WolverineFx.Bobcat.Http` out and act over HTTP through `WhenTracked`, knowing its two limits
in [Bobcat with Wolverine](integrations/wolverine.md#http-without-wolverinefx-bobcat-http).

Otherwise, use a `WolverineFx.Bobcat` whose minimum Bobcat is the release you are on, or the closest one
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
