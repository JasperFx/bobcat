# Version Compatibility

Which Bobcat works with which .NET, test framework, Marten, Polecat, Fisher and Wolverine.

## The one rule

**From 1.0.0-rc.2, Bobcat references no JasperFx, no event store and no Wolverine** (bobcat#744). The
runtime packages — `Bobcat`, `Bobcat.Generators`, the runner adapters and `Bobcat.Supervisor` — put no
JasperFx minimum into your restore, so they can't pin your application's Critter Stack, and they can't
cause the NU1605 / NU1109 downgrades earlier releases did.

The Critter Stack vocabulary (`CritterStackSpec`, `CritterStackScenario`, `EventStores` and the rest of the
`Bobcat.CritterStack` namespace) ships in **`WolverineFx.Bobcat`** from the Wolverine repository. That
package carries the JasperFx.Events coupling now, and the usual Critter Stack rule applies to it:

- Take a Marten, Polecat, Fisher or Wolverine release **built against the JasperFx.Events that
  WolverineFx.Bobcat requires or newer**. NuGet resolves the highest minimum anyone asks for, so an
  older store next to a newer one restores fine and can still fail at run time. A store built against
  an older JasperFx.Events can hit a `TypeLoadException` (`Method '…' in type '…' does not have an
  implementation`) the first time it opens a session.
- If you see that exception, or `IEvent` and other event types that "should" match don't, move the
  stores and Wolverine up to releases built on the JasperFx.Events your graph resolves.

The Event Model still works without a reference: when your spec project has JasperFx.Events (through
WolverineFx.Bobcat or directly), `Bobcat.Generators` emits the Event Model source and a small bridge that
hands Bobcat the JSON to publish. Without JasperFx.Events, no Event Model is generated, exactly as before.

## Bobcat 1.0

| | Requirement | Built and tested against |
|---|---|---|
| .NET | `net9.0` or `net10.0` for the libraries; the `bobcat` tool needs the .NET 10 SDK | .NET 10 |
| `JasperFx`, `JasperFx.Events` | none from 1.0.0-rc.2 (rc.1 and earlier: 2.90.2 or later) | the `bobcat` tool and the Event Model generator's tests: 2.90.2 |
| Marten, Fisher, Polecat | none from Bobcat; through `WolverineFx.Bobcat`, below | in the Wolverine repository since #744 |
| WolverineFx | through `WolverineFx.Bobcat`, below | in the Wolverine repository since #744 |
| Alba | any for Bobcat itself, which does not reference it. `WolverineFx.Bobcat.Http` 6.51.0-alpha.1 requires **9.0.0-rc.2** (below) | |

The "built and tested against" column is what this repository's own suites run on. The store-backed suites
and the samples (real Postgres for Marten, SQLite for Fisher) moved to the Wolverine repository with the
vocabulary in #744.

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

### A build tool that references `Bobcat.Supervisor` gets Bobcat's JasperFx minimum (rc.1 and earlier) {#build-tool-jasperfx}

**From 1.0.0-rc.2 this no longer happens**: `Bobcat` and `Bobcat.Supervisor` take no JasperFx dependency
(bobcat#744, #745), so a build project referencing the supervisor needs no `VersionOverride` for it. Drop the
overrides below when you move to rc.2.

On 1.0.0-rc.1 and earlier, `Bobcat.Supervisor` depends on `Bobcat`, and `Bobcat` requires `JasperFx` and
`JasperFx.Events` at the minimum in [the table below](#earlier-bobcat-releases). That minimum applies to the build project too, even
though the supervisor only uses the retry and isolation types. If the build pins `JasperFx` below
that minimum, restore fails with **NU1605** (a detected package downgrade). With
`CentralPackageTransitivePinningEnabled`, `JasperFx.Events` fails as well, with **NU1109**
(`Detected package downgrade: JasperFx.Events from 2.90.2 to centrally defined 2.90.1`), even
though the build never references it directly.

The build ships nothing, so raise both pins there and leave the product's own JasperFx dependency
alone. `JasperFx.Events` needs a `PackageReference` of its own, which exists only to carry the
override:

```xml
<!-- build/build.csproj -->
<PackageReference Include="Bobcat.Supervisor" />
<PackageReference Include="JasperFx" VersionOverride="2.90.2" />
<!-- Here only to carry the override. Without it, transitive pinning fails with NU1109. -->
<PackageReference Include="JasperFx.Events" VersionOverride="2.90.2" />
```

Override both packages, even if restore only complains about one. **A too-low direct `JasperFx`
pin may not warn at all.** With the `JasperFx.Events` override in place and the `JasperFx` one
left out, `dotnet restore` can succeed and resolve `JasperFx` 2.90.1 under Bobcat's `>= 2.90.2`
with no NU1605 or NU1109. The build then fails at run time instead, for example with a
`TypeLoadException` or `MissingMethodException` from inside Bobcat. A clean restore doesn't prove
the pin is high enough. Check `obj/project.assets.json` (or `dotnet list build/build.csproj package
--include-transitive`) for the `JasperFx` version that was actually resolved.

Don't silence NU1605 or NU1109 with `NoWarn`. That runs Bobcat on a JasperFx older than it was
built against, which is what the table above warns about.

## WolverineFx.Bobcat

`WolverineScenario`, `WolverineSpec` and `WolverineHttpSpec` ship from the Wolverine repository as
**`WolverineFx.Bobcat`**, versioned with Wolverine. Each release names the Bobcat it was built
against as its minimum:

| WolverineFx.Bobcat | Requires Bobcat | Requires WolverineFx |
|---|---|---|
| the release after 6.51.1-alpha.1 (wolverine#5082) | 1.0.0-rc.2 or later | the same version |
| 6.51.1-alpha.1 | 1.0.0-rc.1 exactly: **not** rc.2, which no longer carries the `CritterStackScenario` it derives from | the same version |
| 6.50.0-alpha.1 – 6.51.0-alpha.1 | 0.60.4 or later (on 1.0.0-rc.1, all but `WhenActing`/`WhenActingOn`, below) | the same version |
| 6.49.0-alpha.3 | 0.38.0 or later | 6.49.0 |
| 6.48.x | 0.32.0 or later | the same version |

**For Bobcat 1.0.0-rc.1, use WolverineFx.Bobcat 6.51.1-alpha.1**, the first release built against
Bobcat 1.0. For 1.0.0-rc.2, use the WolverineFx.Bobcat release that carries the Critter Stack
vocabulary (below); 6.51.1-alpha.1 fails on rc.2 with a `TypeLoadException` for
`Bobcat.CritterStack.CritterStackScenario`.

**WolverineFx.Bobcat 6.51.0-alpha.1 on Bobcat 1.0.0-rc.1: `WhenActing` and `WhenActingOn` fail**
(bobcat#742). Bobcat 1.0.0-rc.1 renamed `CritterStackScenario.Store` to `EventStore` (bobcat#709), and
6.51.0-alpha.1 was compiled against the old name, so the act fails with:

```text
System.MissingMethodException: Method not found: '... Bobcat.CritterStack.CritterStackScenario.get_Store()'
```

The fix is to move to 6.51.1-alpha.1, which reads `EventStore`.

**`WolverineFx.Bobcat.Http` 6.51.0-alpha.1 depends on Alba 9.0.0-rc.2** (bobcat#675). A test project
that references Alba 8.x gets NU1605 (a package downgrade) and has to move to the prerelease Alba
to take it, which also drops Alba 8's `ConfigurationOverride`. If you have to stay on Alba 8.x,
leave `WolverineFx.Bobcat.Http` out and act over HTTP through `WhenTracked`, knowing its two limits
in [Bobcat with Wolverine](integrations/wolverine.md#http-without-wolverinefx-bobcat-http).

Otherwise, use a `WolverineFx.Bobcat` whose minimum Bobcat is the release you are on, or the closest one
below it. A `WolverineFx.Bobcat` built against an older Bobcat may compile against a newer one but
is not tested with it.

**From Bobcat 1.0.0-rc.2, WolverineFx.Bobcat also carries `CritterStackSpec` / `CritterStackScenario`** (the
store-only vocabulary, for Marten, Polecat or Fisher without Wolverine messaging), under the same
`Bobcat.CritterStack` namespace. A store-only suite therefore references WolverineFx.Bobcat, which brings
Wolverine with it; that is accepted for now. Writing specifications from an Event Model board
(`import-event-model --specs`) moved to a Wolverine-side command as well.

## Earlier Bobcat releases

The JasperFx minimum is the line that matters when you upgrade.

| Bobcat | `JasperFx` / `JasperFx.Events` minimum | Test framework line |
|---|---|---|
| 1.0.0-rc.2 and later | none | xUnit v3 4.x, MTP 2.x |
| 1.0.0-alpha.1 – 1.0.0-rc.1 | 2.90.2 | xUnit v3 4.x, MTP 2.x |
| 0.60.x | 2.85.0 | xUnit v3 4.x, MTP 2.x |
| 0.39.0 – 0.50.0 | 2.84.1 | xUnit v3 3.x, MTP 1.9 |
| 0.37.0 – 0.38.1 | 2.82.0 | xUnit v3 3.x, MTP 1.9 |
| 0.31.0 – 0.36.1 | 2.81.0 | xUnit v3 3.x, MTP 1.9 |
| 0.21.0 – 0.30.0 | 2.69.3 | xUnit v3 3.x, MTP 1.9 |
| 0.18.0 – 0.20.0 | 2.67.1 | xUnit v3 3.x, MTP 1.9 |

Releases before 0.50.0 also carry the retired Gherkin lane. Bobcat 0.50.0 removed it (see
[Diagnostics](/diagnostics#retired) for the diagnostics that went with it).

## Migrating to 1.0.0-rc.2 {#migrating-to-rc-2}

Bobcat 1.0.0-rc.2 takes no JasperFx or Wolverine dependency (bobcat#744; design:
`design/jasperfx-decoupling.md`). Seven things change for a suite moving from rc.1:

1. **The Critter Stack vocabulary moved to `WolverineFx.Bobcat`**, under the same namespace:
   `CritterStackSpec` / `CritterStackScenario`, `EventStores`, `DocumentStores`, `SpecIdentityAudit`,
   `Preflight.AddContainerChecks` and `services.EventStore()`. Reference `WolverineFx.Bobcat` (a release built
   against rc.2); the `using Bobcat.CritterStack;` lines stay as they are. `RecordBuilding` stays in Bobcat.
2. **`JasperFx.Testing.DispositionKind` and the recovery-hint attributes are now `Bobcat.Resilience.*`**:
   `[ClearsOnRetry]`, `[ClearsOnRecycle]`, `[ClearsInFreshProcess]` and `[NeverRecovers]`, with the same
   constructors and `Because`. Replace `using JasperFx.Testing;` with `using Bobcat.Resilience;`. The supervisor
   reads only Bobcat's attributes now.
3. **`Preflight.Run` returns `PreflightResults`** instead of JasperFx's `EnvironmentCheckResults`. The members
   are the same (`Successes`, `Failures`, `Succeeded()`, `Assert()`); `Assert()` throws a `PreflightException`
   listing every failed check.
4. **`[EventModelTrigger(ActTrigger.Http, …)]` becomes `[EventModelTrigger(ActTrigger.Http, …)]`.** `ActTrigger`
   is in the `Bobcat` namespace and has `TriggerKind`'s member names; the generated Event Model is unchanged.
   This matters only to a project that writes its own scenario vocabulary.
5. **`SpecificationBindings` and Bobcat's `SpecificationManifestAttribute` are deleted** (obsolete since
   bobcat#528). Read a manifest with JasperFx's `SpecificationManifest.In(assembly)`.
6. **`bobcat import-event-model --specs` is gone**, with `--specs-out`, `--specs-namespace`, `--app-project`,
   `--store`, `--bobcat-version` and `--wolverine-version`. The import still writes the stubs and the
   `EventModelDefinition` and merges into them on a re-import; specifications come from the Wolverine-side
   command.
7. **A suite that got JasperFx transitively through Bobcat and uses it directly now needs its own reference** —
   `JasperFx`, `JasperFx.Events`, or (for the vocabulary) `WolverineFx.Bobcat`. The same goes for
   `Microsoft.Extensions.Configuration.*` and other packages JasperFx used to bring in; Bobcat itself still
   brings Spectre.Console and the `Microsoft.Extensions.DependencyInjection` / `Hosting` abstractions.

A build project that referenced `Bobcat.Supervisor` can drop the `JasperFx` / `JasperFx.Events`
`VersionOverride`s it needed on rc.1 ([above](#build-tool-jasperfx)).
