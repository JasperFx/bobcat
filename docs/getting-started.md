# Getting Started

Bobcat is a toolset for authoring, supervising, and executing integration tests in .NET.

This page gets one Gherkin scenario running end to end. It takes about five minutes.

## 1. The project

A spec project is an ordinary executable with three packages and one item group:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Bobcat" />
  <PackageReference Include="Bobcat.Mtp" />
  <PackageReference Include="Bobcat.Generators" />
  <AdditionalFiles Include="Features/**/*.feature" />
</ItemGroup>
```

Three of those lines do work that is easy to miss:

- **`Bobcat.Generators`** compiles your `.feature` files. Without it they are inert.
- **`<AdditionalFiles>`** is what the generator reads. Without it the project still compiles — it
  just compiles no scenarios, and a green build over an empty suite is the quietest way to get
  this wrong.
- **The two MSBuild properties** make `dotnet test` recognize the project. Without them it
  restores, says nothing about tests, and exits 0.

## 2. Your first feature

`Features/Customers.feature`:

```gherkin
Feature: Customer registration

  Scenario: A new customer is created
    Given a customer named "Ada"
    When the customer list is requested
    Then the response contains "Ada"
```

## 3. The fixture

A fixture is a plain class that **must** extend `Bobcat.Fixture`. Bind each step to a method:

```csharp
using Bobcat;

[FixtureTitle("Customer registration")]
public class CustomerFixture : Fixture
{
    private readonly List<string> _customers = [];
    private string _response = "";

    [Given("a customer named {string}")]
    public void Customer(string name) => _customers.Add(name);

    [When("the customer list is requested")]
    public void ListRequested() => _response = string.Join(", ", _customers);

    [Then("the response contains {string}")]
    public void ResponseContains(string expected)
    {
        if (!_response.Contains(expected))
            throw new Exception($"Expected '{expected}' in '{_response}'");
    }
}
```

Two binding rules decide whether this works at all:

- **Extending `Fixture` is not optional.** The generator's discovery is literally "inherits from
  `Bobcat.Fixture`", so a class carrying `[FixtureTitle]` and nothing else matches no feature —
  and the symptom is silence, not an error.
- **Every step must bind.** An unbound step is a **build error** (`BOBCAT002`), not a pending
  test. If you are coming from SpecFlow or Reqnroll, this is the difference you will feel first.

`[FixtureTitle]` is only needed when the class name does not derive to the feature title. The
convention splits on camel humps, so `CustomerRegistrationFixture` would bind to "Customer
registration" on its own; `CustomerFixture` does not, which is why it says so here. A feature with
no fixture is `BOBCAT001`.

## 4. Run it

```bash
dotnet test
```

```
Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1
```

Your IDE's test explorer will show the scenario as a test too — one scenario is one test node — so
you can run and debug it there.

## Going further

**Point it at a real application.** A calculator does not need a host; an integration suite does,
and that is what Bobcat is for. `Bobcat.Alba` runs your ASP.NET Core app in-process and lets steps
drive it over HTTP:

```csharp
var result = await Context!.PostJsonAsync<CreateCustomer, Customer>(
    "/customers", new CreateCustomer(name));
```

See [Bobcat with Alba](integrations/alba.md), and
[Resources](resources.md) for how a host, a database or a set of containers is started, reset and
disposed around your scenarios.

**Prefer C# to `.feature` files?** [Specs from tests you already have](marker-steps.md) turns
existing xUnit v3 or TUnit tests into specifications with marker comments and decorated helpers,
one class at a time — they keep running on their own runner, and render, supervise and report
alongside Gherkin ones.

## Next

- **[The Tutorials](tutorials/)** — the whole documentation set organized by what you are trying to accomplish, rather than by what Bobcat is made of. Start there if you are not sure which page you want
- [Integrating Bobcat Gherkin](integrating-gherkin.md) — the two ways to make a spec project executable, and when to pick each
- [Specs From Existing Tests](marker-steps.md) — marker comments and `[BobcatStep]` helpers, for a suite you would rather not rewrite
- [The Run Lifecycle](run-lifecycle.md) and [Resources](resources.md) — what Bobcat does to your application, and when
- [Editor Integration](editor-integration.md) — step completion and go-to-definition in VS Code and Rider
- [Make Existing Integration Tests More Reliable](tutorials/reliable-integration-testing.md) — the supervisor: worker-process splitting, per-lane resource isolation, retry budgets
- [What a Run Publishes](monitor-design.md) — the monitor wire contract, `BOBCAT_MONITOR*`, and the seams that emit it
- [The `bobcat` Tool](bobcat-tool.md) — reading, validating and importing Event Model files
