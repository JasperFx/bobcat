# Getting Started

Bobcat is a toolset for authoring, supervising, and executing integration tests in .NET. A Bobcat
specification is an ordinary test -- xUnit v3, TUnit, NUnit or MSTest -- that also renders as a
Given/When/Then specification and reports itself, step by step, to whatever is watching the run.

<!-- JEREMY -->
This page gets one specification running end to end against an event sourced application on Marten,
Polecat or Fisher. It takes about five minutes.

## 1. The project

A spec project is an ordinary xUnit v3 test project with three Bobcat packages:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="xunit.v3" />
  <PackageReference Include="Microsoft.NET.Test.Sdk" />
  <PackageReference Include="xunit.runner.visualstudio" />

  <PackageReference Include="Bobcat" />
  <PackageReference Include="Bobcat.Generators" />
  <PackageReference Include="Bobcat.Xunit" />

  <ProjectReference Include="..\MyApp\MyApp.csproj" />
</ItemGroup>
```

Three of those lines do work that is easy to miss:

- **`Bobcat.Generators`** is what turns a call to a decorated step helper into a recorded step. Its
  package carries the interceptor opt-in, so there is no csproj line to remember. Without it every
  step is an ordinary method call, a `SpecAssert` inside one reports to nothing, and the suite passes
  having produced no specification at all.
- **`Bobcat.Xunit`** is the runner adapter. It opens a scenario around each test in a
  `[BobcatFeature]` class and closes it with the verdict xUnit reported. Its package also registers
  the per-run bracket with the test platform, so there is no code to write. TUnit, NUnit and MSTest
  have their own adapters: [Bobcat with TUnit](tunit.md), [Bobcat with NUnit](nunit.md),
  [Bobcat with MSTest](mstest.md).
- **`Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`** are what Rider and Visual Studio
  discover tests through. Leave them out and the suite still runs from the command line and shows
  *no tests* in the IDE -- which is why the generator warns with **BOBCAT033** when they are missing.

## 2. Host the application once

Bobcat doesn't own your test lifecycle any more -- your test runner does. Boot the application in an
xUnit assembly fixture, exactly as you would for any integration test. This one is from the
`samples/BankAccountES` sample, trimmed:

```csharp
using Alba;
using Bobcat.CritterStack;

[assembly: AssemblyFixture(typeof(AppHost))]

public class AppHost : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await AlbaHost.For<Program>();

        // Empties every event store the host registers -- documents AND streams -- through
        // JasperFx.Events, so it reads the same on Marten, Polecat and Fisher
        await Host.ResetEventStoresAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }
}
```

Any `IHost` works: Alba is only here because the sample also drives HTTP endpoints. A
`CritterStackSpec` can be built from an `IServiceProvider` instead, for a test that has a container
and no host.

::: tip Resetting between specifications
Mint new ids in every specification and most suites never need a reset between tests. When one does,
call `ResetAsync()` on the spec (it clears every event store and forgets the last act), or
`host.ResetEventStoresAsync()` from your own fixture.
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
- [Migrating from Gherkin](migrating-from-gherkin.md) -- for a suite written against Bobcat's retired `.feature` lane
- [Make Existing Integration Tests More Reliable](tutorials/reliable-integration-testing.md) -- the supervisor: worker-process splitting, per-lane resource isolation, retry budgets
- [What a Run Publishes](monitor-design.md) -- the monitor wire contract, `BOBCAT_MONITOR*`, and the seams that emit it
- [The Resident Runner](resident-runner.md) -- keeping a suite available to a run console, so specs run when somebody asks
- [The `bobcat` Tool](bobcat-tool.md) -- importing Event Model boards, and the resident runner
