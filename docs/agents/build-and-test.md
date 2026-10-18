# Build, test and packages

### Solutions, packages and the build

- **Solutions are `.slnx`, everywhere** — `bobcat.slnx`, `spikes/mtp-orchestration/MtpSpike.slnx`,
  `spikes/transport-compliance/TransportComplianceSpike.slnx` (#564: inherited specs, `findings.md`), and
  `spikes/leadership-telemetry/LeadershipTelemetrySpike.slnx` (#563: telemetry on failure, `findings.md`). There are no `.sln` files
  left; converted with `dotnet sln migrate` on 2026-09-28. The spikes are deliberately **not** in
  `bobcat.slnx`. The samples moved to the Wolverine repo in #744.
- **Central Package Management, one file per tree**, never an inline `Version=` on a
  `PackageReference`: `src/Directory.Packages.props` and `spikes/mtp-orchestration/Directory.Packages.props` (kept
  apart because the spike measured MTP 2.x, which src cannot take). **`build/` opts out** in its own
  `Directory.Packages.props` and pins inline, like the JasperFx repo's build: the Nuke bootstrap
  stays self-contained, and the root file must not grow Nuke.Common.
- **The Nuke build (`build/`) is the one definition of CI** — `Build.cs` (Restore / Compile / Test /
  `CI` / Pack / Docker) and `UiSmoke.cs` (`UiSmoke`). `tests.yml` runs `./build.sh CI`,
  `publish.yml` runs `./build.sh CI Pack`, `ui-smoke.yml` runs `./build.sh UiSmoke`. The `Samples`
  targets and `samples.yml` moved to the Wolverine repo with the samples (#744). **Change what CI does in `build/`,
  not in a workflow** — the workflows only supply services (Postgres) and environment.
  Configuration defaults to Debug locally and Release on a CI server.
  - `Docker` (`docker compose up -d --wait`) is never a dependency of `Test`: someone pointing
    `BOBCAT_POSTGRES` at their own database must not have Docker started for them.
  - **`Pack` writes to `artifacts/packages` and `Clean` touches only that.** `artifacts/local-feed`
    is a hand-maintained NuGet feed consumed by the Wolverine repo's samples and its CI branch — never clean `artifacts/` wholesale. `publish.yml` pushes
    `artifacts/packages/*.nupkg`.
  - **Every package carries the root `LICENSE`** (`PackageLicenseFile`, packed from
    `src/Directory.Build.props` by path — not copied, unlike `src/icon.png`). It replaced
    `PackageLicenseExpression` (NuGet refuses both, NU5033), so nuget.org shows a license *file*.

**Version pin that matters:** `Microsoft.Testing.Platform` is held at **2.5.1** because that is
what `xunit.v3` 4.0.2 builds against (its runner package is `xunit.v3.core.mtp-v2`), and
`TUnit.Engine` 1.74.0 wants the same. It was 1.9.1 under `xunit.v3` 3.2.2 (`xunit.v3.core.mtp-v1`)
until bobcat#470; the platform and xUnit move together. MTP 2.x refuses the VSTest bridge on the
.NET 10 SDK, so the root `global.json` opts `dotnet test` into its MTP mode: pass `--solution` /
`--project` (never a positional path), give test-host options directly (no `--` separator), and
note that mode collects every project with `IsTestingPlatformApplication=true` — `IsTestProject=false`
no longer opts a host out (see `Bobcat.Xunit.Samples.csproj`).

**No JasperFx or Wolverine in the runtime packages (#744).** `Bobcat` (and, with #745,
`Bobcat.Supervisor`) reference neither; `BobcatAssemblyReferenceTests` guards core. JasperFx(.Events) is
referenced by the tools (`Bobcat.EventModel`, `Bobcat.Console`, `Bobcat.Ui`) and by the test hosts that
compile the Event Model (`Bobcat.Generators.Tests`, `Bobcat.Xunit.Samples`), each directly — core no longer
carries it to them. Core's own Spectre.Console and Microsoft.Extensions.*.Abstractions references are pinned
low in `src/Directory.Packages.props`, because a test framework's floor is every consumer's floor.

**Database-backed tests:** the Postgres-backed `Bobcat.CritterStackScenario.Tests` moved to the Wolverine
repo with the vocabulary (#744). The compose file and the `Docker` target stay.

## Package Structure

Every shipped package is `<IsPackable>true</IsPackable>` in its own csproj (src/ defaults to false)
and is packed by the Nuke `Pack` target; `./build.sh Pack` lists exactly these nine.

| Package | Target | Responsibility |
|---------|--------|----------------|
| **Bobcat** | net9.0; net10.0 | Runtime: the projected specification recorder, verification (`TableRunner`, `PropertyCells`, `SetVerificationComparer`, `ObjectSetVerification`, partial objects, `RecordBuilding`), rendering, the monitor wire, the resident runner, the resilience vocabulary and `Preflight`. No JasperFx or Wolverine dependency (#744); the Critter Stack vocabulary ships in WolverineFx.Bobcat |
| **Bobcat.Generators** | netstandard2.0 | Source generator: step interceptors, marker-comment specs, declared values, specification bindings, the Event Model descriptor and its publishing bridge, BOBCAT032 table checks |
| **Bobcat.Supervisor** | net9.0; net10.0 | Drives MTP hosts as worker processes; retry/isolation policy, parallel lanes |
| **Bobcat.Xunit** | net9.0; net10.0 | Projects xUnit v3 `[Fact]`/`[Theory]` tests into the Bobcat model (marker steps) |
| **Bobcat.TUnit** | net9.0; net10.0 | The same projection for TUnit `[Test]` methods |
| **Bobcat.NUnit** | net9.0; net10.0 | The same projection for NUnit `[Test]`/`[TestCase]` methods (an `ITestAction`) |
| **Bobcat.MSTest** | net9.0; net10.0 | The same projection for MSTest `[TestMethod]`s — references no MSTest; ships global hooks as source |
| **Bobcat.EventModel** | net9.0; net10.0 | Imports an eventmodelers.ai emlang board and writes it as C# stub types plus an `EventModelDefinition`; the curated YAML format is retired (#406) |
| **Bobcat.Console** | net10.0 | The `bobcat` global tool: `run` (the default), `preview`, `pick` and `watch` over a spec project, `import-event-model`, `resident` for the out-of-process resident runner, `ui` (the run board and Event Model canvas, `src/Bobcat.Ui`), `watch-event-model`, and the agent queries (`list-runs`, `run-status`, `failing-tests`, `failing-spec`, `flaky-ledger`, `await-run`, `export-run`, `event-model`, `slice-coverage`). Packable only with `EmbedFrontend=true`, so it never ships without its page |

`design/history/acceptance-coverage-map.md` maps the retired acceptance suite onto
`Bobcat.Projected.Acceptance.Tests`; `Bobcat.Supervisor.Tests` covers the rest.

**Retired as a package (#407):** `Bobcat.Wolverine`. Its act, warm-up, transport draining and
`WolverineScenario` ship in **`WolverineFx.Bobcat`** from the Wolverine repo, and since #744 so does the
store-only `CritterStackScenario`/`CritterStackSpec`, so no Bobcat runtime package depends on JasperFx or
Wolverine.

Not packed: `Bobcat.Xunit.Samples` and `Bobcat.Supervisor.ProjectedWorker` are hosts the tests drive;
`Bobcat.Ui` ships inside the tool, never as a package of its own.

## Consuming Project Setup

```xml
<PackageReference Include="Bobcat" />
<PackageReference Include="Bobcat.Generators" />
<PackageReference Include="Bobcat.Xunit" />   <!-- or Bobcat.TUnit / Bobcat.NUnit / Bobcat.MSTest -->
```

The `Bobcat.Generators` package's `buildTransitive/Bobcat.Generators.props` opts the project into
`InterceptorsNamespaces=Bobcat.Generated` (without it, BOBCAT037). A `ProjectReference` to the
generator (`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`) takes no build assets, so
in-repo projects import that props file — and `Bobcat.Xunit.props` — themselves. `BobcatProjectAssertions=true` opts a project into projected
Shouldly assertions.
