# Getting Started

Bobcat is a toolset for authoring, supervising, and executing integration tests in .NET. A Bobcat
specification is an ordinary test -- xUnit v3, TUnit, NUnit or MSTest -- that also renders as a
Given/When/Then specification and reports itself, step by step, to whatever is watching the run.

This page gets one specification running end to end. It takes about five minutes.

::: tip Wolverine, Marten, Polecat or Fisher?
Bobcat's core is framework-neutral. The Critter Stack vocabulary -- `CritterStackSpec`, `WolverineSpec`,
`WolverineHttpSpec`, `GivenEvents`, `WhenReceived`, `ThenEvents` and the rest -- ships in
**WolverineFx.Bobcat** and is documented in the Wolverine docs: [Testing with Bobcat](https://wolverinefx.net/guide/testing#with-wolverinefx-bobcat). The
project setup below applies to it unchanged.
:::

## 1. The project

A spec project is an xUnit v3 test project on Microsoft.Testing.Platform with three Bobcat packages:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <IsTestProject>true</IsTestProject>
  <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>

  <!-- the same version for all three Bobcat packages -->
  <BobcatVersion>1.0.0-rc.2</BobcatVersion>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="xunit.v3" Version="4.0.2" />
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
  <PackageReference Include="xunit.runner.visualstudio" Version="4.0.1" PrivateAssets="all" />

  <PackageReference Include="Bobcat" Version="$(BobcatVersion)" />
  <PackageReference Include="Bobcat.Generators" Version="$(BobcatVersion)" PrivateAssets="all" />
  <PackageReference Include="Bobcat.Xunit" Version="$(BobcatVersion)" />

  <ProjectReference Include="..\MyApp\MyApp.csproj" />
</ItemGroup>
```

Which `xunit.v3`, Microsoft.Testing.Platform and
Critter Stack versions go with which Bobcat is on [Version Compatibility](/compatibility).

And a `global.json` at the root of the repository (or anywhere above the spec project), so
`dotnet test` runs the project on Microsoft.Testing.Platform:

```json
{
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

`xunit.v3` 4.x runs on Microsoft.Testing.Platform 2.x, which refuses the old VSTest bridge on the
.NET 10 SDK, so without that file `dotnet test` fails before a test runs. In that mode the project
is named with an option and test-host options go straight on the command line:
`dotnet test --project MySpecs --filter-class "*Wallet*"`. The other changes from xUnit v3 3.x are
listed under [Moving to xUnit v3 4.x](xunit.md#moving-to-xunit-4).

Several of those lines do work that is easy to miss:

- **`Bobcat.Generators`** is what turns a call to a decorated step helper into a recorded step. Its
  package carries the interceptor opt-in (`InterceptorsNamespaces` gains `Bobcat.Generated`), so
  there is no csproj line to remember. If the opt-in is missing anyway -- see the next section --
  the build stops with **BOBCAT037** at your first step call rather than passing without recording
  anything.
- **`Bobcat.Xunit`** is the runner adapter. It opens a scenario around each test in a
  `[BobcatFeature]` class and closes it with the verdict xUnit reported. Its package also registers
  the per-run bracket with the test platform, so there is no code to write. TUnit, NUnit and MSTest
  have their own adapters: [Bobcat with TUnit](tunit.md), [Bobcat with NUnit](nunit.md),
  [Bobcat with MSTest](mstest.md).
- **`Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`** are what Rider and Visual Studio
  discover tests through. Leave them out and the suite still runs from the command line and shows
  *no tests* in the IDE -- which is why the build warns with **BOBCAT033** when they are missing
  (`<BobcatSuppressTestSdkWarning>true</BobcatSuppressTestSdkWarning>` if the suite only ever runs
  from the command line).

::: tip Specifications from a board
Writing this project and a specification per board example from an Event Model is a Wolverine-side
command since Bobcat 1.0.0-rc.2 (it writes `WolverineFx.Bobcat` specs). `bobcat import-event-model`
still writes the stubs and the `EventModelDefinition`; see [`import-event-model`](bobcat-tool.md#import-event-model).
:::

### Referencing Bobcat by ProjectReference

Working inside the Bobcat repository, or anywhere you reference Bobcat's projects rather than its
packages, you lose two settings the packages carry in their `buildTransitive` props. A
`ProjectReference` takes no build assets, so the generator arrives as an analyzer without its
interceptor opt-in, and `Bobcat.Xunit` arrives without its test-platform registration.

Don't restate them by hand. Import the packages' own props:

```xml
<Import Project="path/to/src/Bobcat.Generators/buildTransitive/Bobcat.Generators.props" />
<Import Project="path/to/src/Bobcat.Xunit/buildTransitive/Bobcat.Xunit.props" />
```

Forget the first one and the build stops with **BOBCAT037**, once, at your first step call. It used to
be a `CS9137` per call site in a generated file, and before that the danger was a project that built
anyway and passed without recording a thing. Forget the second and `[BobcatFeature]` on its own no
longer records a class (the props apply `[assembly: RecordBobcatFeatures]`), and the run bracket
falls back to one per process (see [Warm Projected Runs](warm-projected-runs.md)).

## 2. Host the application, if there is one

Bobcat doesn't own your test lifecycle -- your test runner does. Boot whatever the specification
needs the way you would for any integration test: an xUnit assembly, collection or class fixture,
`IAsyncLifetime`, TUnit's hooks. Alba is a good way to host an ASP.NET Core application in-process.
The specification below needs nothing hosted, so it skips this step.

## 3. Your first specification

A step is a method you decorate with `[Given]`, `[When]` or `[Then]`. Put the steps on a class of
their own -- a *grammar* -- so every specification that calls them reads the same way:

<<< ../src/Bobcat.Xunit.Samples/Grammars/CalculatorSteps.cs#calculator-steps{cs}

Then write an ordinary test that calls them, in a class marked `[BobcatFeature]`. (The sample
project references Bobcat by project rather than by package, so it states `[BobcatScenario]` as
well; with the packages, `[BobcatFeature]` alone is enough on xUnit.)

<<< ../src/Bobcat.Xunit.Samples/Specs/GettingStartedSpecs.cs#getting-started-spec{cs}

Every call to a decorated helper is a step: the generator intercepts it at build time, and each
`{placeholder}` is filled from the argument of that name. `SpecAssert.Check` records an
expected/actual cell on the step in progress. A wrong is *gathered* rather than thrown, so a
specification shows every disagreement, and the adapter fails the test once at the end. The helpers
are `internal` rather than `private` because the interceptor is an extension method, which cannot
reach a private member ([BOBCAT039](diagnostics.md#bobcat039)).

This is Storyteller's own first example, recreated as a projected test in `src/Bobcat.Xunit.Samples`,
with the grammar cut down to the four steps this specification uses. `CalculatorGrammar.cs` next to
it is the full one.

## 4. Run it

```bash
dotnet test
```

In a terminal, with no run console listening, the run prints the specification it produced:

```
Feature: Calculator, getting started
═════════════════════════════════════
  using sentences OK
  ─────────────────────────
    ✓ Given Start with 3 (1ms)
    ✓ When  Multiply by 2
    ✓ Then  The value should be 6 (3ms)
    ✓ And   Adding 2 to 3 should equal 5
  Succeeded with Rights: 4, Wrongs: 0, Errors: 0
```

Your IDE's test explorer shows each specification as the ordinary test it is, so you can run and
debug it there.

## Next

- **[The Tutorials](tutorials/)** -- the documentation organized by what you are trying to accomplish
- [Writing Specifications](marker-steps.md) -- step helpers, marker comments, `SpecAssert`, tables, and binding a spec to an Event Model slice
- [Testing with Bobcat](https://wolverinefx.net/guide/testing#with-wolverinefx-bobcat) (Wolverine docs) -- the Critter Stack vocabulary in WolverineFx.Bobcat, for Wolverine, Marten, Polecat and Fisher
- [Make Existing Integration Tests More Reliable](tutorials/reliable-integration-testing.md) -- the supervisor: worker-process splitting, per-lane resource isolation, retry budgets
- [What a Run Publishes](monitor-design.md) -- the monitor wire contract, `BOBCAT_MONITOR*`, and the seams that emit it
- [The Resident Runner](resident-runner.md) -- keeping a suite available to a run console, so specs run when somebody asks
- [The Bobcat UI](bobcat-ui.md) -- `bobcat ui`: the live run board and the Event Model canvas, in your browser
- [The `bobcat` Tool](bobcat-tool.md) -- importing Event Model boards, the UI, and the resident runner
