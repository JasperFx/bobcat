# Behavior Driven Development with Gherkin

**What you will build:** a spec project that turns `.feature` files into compiled tests, with no
runtime reflection and no step registry to maintain.

**What you need:** .NET 9 or 10, and a project to test.

## The shape of it

Bobcat compiles Gherkin. A `.feature` file is an input to a source generator, not a document parsed
at runtime — every step becomes a direct call to a fixture method at build time. That has one
consequence worth internalizing before you start: **a step that does not bind is a build error, not
a pending test.** If you are coming from SpecFlow or Reqnroll, this is the difference you will feel
first.

## 1. The project

A spec project is an ordinary executable with three packages and one item group:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Bobcat" />
  <PackageReference Include="Bobcat.Generators" />
  <PackageReference Include="Bobcat.Mtp" />
  <AdditionalFiles Include="Features/**/*.feature" />
</ItemGroup>
```

The `AdditionalFiles` line is what makes the generator see your features. Without it the project
still compiles — it just compiles nothing, and you get a green build containing no tests at all.
It is the quietest way to get this wrong.

`Bobcat.Mtp` makes the project a Microsoft.Testing.Platform host, so `dotnet test` and IDE test
explorers see each scenario as a test. See [Integrating Bobcat Gherkin](../integrating-gherkin.md#dotnet-test)
for what that buys you and how to configure the suite.

## 2. The feature

```gherkin
Feature: Calculator

  @arithmetic
  Scenario: Add two numbers
    Given the left operand is 25
    And the right operand is 17
    When the operands are added
    Then the result is 42
```

## 3. The fixture

A fixture is a plain class that **must** extend `Bobcat.Fixture`:

```csharp
using Bobcat;

public class CalculatorFixture : Fixture
{
    private int _left, _right, _result;

    [Given("the left operand is {int}")]
    public void TheLeftOperandIs(int value) => _left = value;

    [Given("the right operand is {int}")]
    public void TheRightOperandIs(int value) => _right = value;

    [When("the operands are added")]
    public void Added() => _result = _left + _right;

    [Then("the result is {int}")]
    public void TheResultIs(int expected)
    {
        if (_result != expected) throw new Exception($"Expected {expected} but was {_result}");
    }
}
```

`{int}` is a [Cucumber expression](https://github.com/cucumber/cucumber-expressions) capture, bound
to the parameter by position.

Two binding rules decide whether this works at all:

- **Extending `Fixture` is not optional.** The generator's discovery is literally "inherits from
  `Bobcat.Fixture`". A class that carries `[FixtureTitle]` but extends nothing matches no feature,
  and the symptom is silence — the project compiles and `list` reports no features.
- **The fixture is matched to the feature by title.** `CalculatorFixture` derives to "Calculator"
  by splitting on camel humps. When the names do not line up, say so explicitly with
  `[FixtureTitle("Customer registration")]`; the generator will tell you (`BOBCAT001`) rather than
  guess.

## 4. The entry point

If you referenced `Bobcat.Mtp`, you need no `Main` at all — the generator emits one. Skip to
step 5.

For a plain console runner instead, scan the assembly for the features the generator emitted:

```csharp
using System.Reflection;
using Bobcat.Runtime;

return await BobcatRunner.Run(args, runner =>
{
    runner.ScanForFeatures(Assembly.GetExecutingAssembly());
});
```

`ScanForFeatures` is opt-in, and forgetting it is the other quiet failure: every command runs, and
finds nothing.

## 5. Run it

```bash
dotnet run -- list        # features and scenarios, nothing executed
dotnet run -- preview     # every step WITH the method it bound to
dotnet run -- run         # execute
```

`preview` is the one to reach for when a step matched something you did not expect — it prints the
binding and where each parameter's value came from:

```
    ○ Given the left operand is 25
      ↳ CalculatorFixture.TheLeftOperandIs — "the left operand is {int}"
        value ← "25" (capture)
```

It never starts a resource, so it works with the database down. The full command surface is in
[Integrating Bobcat Gherkin](../integrating-gherkin.md#command-line-runner).

## 6. Point it at a real system

A calculator does not need a host. An integration suite does, and that is what Bobcat is for. Add
`Bobcat.Alba` and register the host as a resource, then drive it over HTTP from your steps:

```csharp
var result = await Context!.PostJsonAsync<CreateCustomer, Customer>("/customers", new CreateCustomer(name));
```

`Context` is `IStepContext?`, so the `!` is load-bearing, and both type arguments are required —
`TResponse` cannot be inferred from the call.

Wiring a real host has a playbook of its own, including eighteen footguns found by actually doing
it: [Wiring a Real Host](../wiring-a-real-host.md). The two that bite first are giving the resource
a reset hook so scenarios do not inherit each other's data, and waiting for cascaded messages
before asserting.

## 7. Share steps between features

Once you have more than one fixture, the steps they have in common belong in a grammar module that
each fixture composes with `[IncludeGrammars]` rather than in a base class:

```csharp
[IncludeGrammars(typeof(DocumentGrammars))]
public class OrderFixture : Fixture { }
```

Modules can take constructor parameters, and the shipped ones cover documents, sagas and HTTP.
See [Composing Grammar Modules](../composing-grammars.md).

## Where to go next

- Tables for bulk setup and set verification — [Data Intensive Specifications](data-intensive-specifications.md)
- The same engine without Gherkin — [Specifications with Code](specifications-with-code.md)
- Step completion and go-to-definition in your editor — [Integrating Bobcat with Your IDE](ide-integration.md)
