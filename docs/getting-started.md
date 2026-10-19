# Getting Started

Bobcat is a toolset for authoring, supervising, and executing integration tests in .NET. A Bobcat
specification is an ordinary test -- xUnit v3, TUnit, NUnit or MSTest -- that also renders as a
Given/When/Then specification and reports itself, step by step, to whatever is watching the run.

<!-- JEREMY -->
This page gets one specification running end to end against an event sourced application on Marten,
Polecat or Fisher. It takes about five minutes.

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
  <BobcatVersion>1.0.0</BobcatVersion>
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

## 2. Host the application once

Bobcat doesn't own your test lifecycle any more -- your test runner does. Boot the application in an
xUnit assembly fixture, exactly as you would for any integration test. This one is from the
BankAccountES sample, which lives in the [Wolverine repository](https://github.com/JasperFx/wolverine)
with the other samples since Bobcat 1.0.0-rc.2 (the specification below calls its own fixture `AppHost`):

```cs
public class BankAccountHost : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await AlbaHost.For<Program>();

        // Not load-bearing for correctness: every spec mints its own ids, so no run can collide
        // with the last one. It is here so the event store does not grow without bound across runs.
        // ResetEventStoresAsync empties every store the host registers -- documents AND streams --
        // through JasperFx.Events.IEventStore, so it reads the same on Marten and Fisher.
        await Host.ResetEventStoresAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }
}
```

The sample registers it once for the run, in its `AssemblyAttributes.cs`:

```csharp
[assembly: AssemblyFixture(typeof(BankAccountHost))]
```

Any `IHost` works: Alba is only here because the sample also drives HTTP endpoints. A
`CritterStackSpec` can be built from an `IServiceProvider` instead, for a test that has a container
and no host.

::: tip Resetting between specifications
Mint new ids in every specification and most suites never need a reset between tests. When one does,
call `ResetAsync()` on the spec (it clears every event store and forgets the last act), or
`host.ResetEventStoresAsync()` from your own fixture. Both pause the async daemon while the stores are
emptied and resume it afterwards, so a host running async projections needs no store-specific reset.
:::

## 3. Your first specification

Derive from `CritterStackSpec`, Bobcat's store-only Given/When/Then vocabulary, and put
`[BobcatFeature]` on the class:

```csharp
using Bobcat;
using Bobcat.CritterStack;
using Microsoft.Extensions.DependencyInjection;

[BobcatFeature("Wallets")]
public class crediting_a_wallet(AppHost app) : CritterStackSpec(app.Host)
{
    [Fact]
    public async Task a_credit_is_recorded()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"));

        await WhenActing("the wallet is credited",
            services => services.GetRequiredService<Wallets>().Credit(id, 10));

        ThenEvents(new WalletCredited(id, 10));
        await ThenAggregate<Wallet>(id, Specify<Wallet>().With(x => x.Balance, 10m));
    }
}
```

Every one of those calls is a step. `GivenEvents` appends the history, `WhenActing` runs the act in a
fresh service scope and captures what it appended (or the exception it threw), and the `Then` steps
judge what happened. A wrong is *gathered* on its step rather than thrown, so a specification shows
every disagreement, and the adapter fails the test once at the end.

For a Wolverine application, derive from `WolverineSpec` (WolverineFx.Bobcat) instead and act with
`WhenReceived(command)` through Wolverine's tracked session. Everything else reads the same. See
[Bobcat with Wolverine](integrations/wolverine.md).

## 4. Run it

```bash
dotnet test
```

In a terminal, with no run console listening, the run prints the specification it produced:

```
Feature: Wallets
════════════════

  a credit is recorded OK
  ──────────────────────────────
    ✓ Given the Wallet stream has already recorded WalletOpened(Id: …, Owner: Ann)
    ✓ When the wallet is credited
    ✓ Then WalletCredited(…) is emitted
    ✓ And the Wallet matches
```

Your IDE's test explorer shows each specification as the ordinary test it is, so you can run and
debug it there.

## Next

- **[The Tutorials](tutorials/)** -- the documentation organized by what you are trying to accomplish
- [Writing Specifications](marker-steps.md) -- step helpers, marker comments, `SpecAssert`, tables, and binding a spec to an Event Model slice
- [Bobcat with Marten, Polecat and Fisher](integrations/marten.md) -- the store-only vocabulary, with no message bus
- [Bobcat with Wolverine](integrations/wolverine.md) -- the whole Critter Stack vocabulary, step by step
- [Make Existing Integration Tests More Reliable](tutorials/reliable-integration-testing.md) -- the supervisor: worker-process splitting, per-lane resource isolation, retry budgets
- [What a Run Publishes](monitor-design.md) -- the monitor wire contract, `BOBCAT_MONITOR*`, and the seams that emit it
- [The Resident Runner](resident-runner.md) -- keeping a suite available to a run console, so specs run when somebody asks
- [The Bobcat UI](bobcat-ui.md) -- `bobcat ui`: the live run board and the Event Model canvas, in your browser
- [The `bobcat` Tool](bobcat-tool.md) -- importing Event Model boards, the UI, and the resident runner
