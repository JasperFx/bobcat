# Specifications with Code

**What you will build:** specifications with no `.feature` files — written in C#, rendered and
reported exactly like Gherkin ones.

Gherkin earns its keep when a non-developer reads the specs. When nobody does, its cost is a second
file, a binding layer, and a grammar to maintain. Bobcat has two ways to skip it, and they solve
different problems.

## Which one you want

| | [Code-first specifications](#code-first-specifications) | [Specs from tests you already have](#specs-from-tests-you-already-have) |
|---|---|---|
| You are | writing something new | keeping a suite you already trust |
| You write | a `Specification` class with `[Scenario]` methods | your existing xUnit v3 or TUnit tests, annotated |
| It runs on | Bobcat's engine | your existing runner |
| Reach for it when | you want Bobcat's reporting without Gherkin's ceremony | rewriting the suite is not on the table |

Both render identically to Gherkin, and both appear on the Event Model the same way.

## Code-first specifications

A specification **is** a fixture — one fresh instance per scenario, same `Context`, same lifecycle
hooks:

```csharp
public class OrderSagaSpecs : Specification              // feature title: "Order Saga"
{
    [Scenario("Starting an order")]
    public void starting_an_order()
    {
        var orderId = Guid.NewGuid().ToString();

        var run = When("StartOrder is received", ctx => ctx.InvokeMessageAndWaitAsync(new StartOrder(orderId), "app"))
            .WithRows(new StartOrder(orderId));

        Then("the Order saga document", ctx => load(ctx, orderId)).ShouldNotBeNull();
        Then("the OrderTimeout scheduled by the saga", () => run.Value.Scheduled.SingleMessage<OrderTimeout>().Id).ShouldBe(orderId);
        ThenRows("the open orders", ctx => query(ctx)).KeyedBy("Id").ShouldMatch(new { Id = orderId, Completed = false });
    }
}
```

Register it beside your features:

```csharp
runner.AddSpecification<OrderSagaSpecs>();        // or ScanForSpecifications(assembly)
```

### The one thing to understand: compose, then execute

The scenario method does not run your test. It runs at plan-build time and **declares** steps,
which the engine then executes with the same timeouts, continuation rules and observers every other
scenario gets.

Two consequences you will hit in the first hour:

- **The method body cannot `await` a step's outcome.** A `Given`/`When` that returns a value hands
  back a `Captured<T>`, read as `.Value` *inside a later step*. Reading one too early throws with
  an explanation rather than a null reference.
- **Anything needing the step context belongs in a step body**, not in the method around them.

An exception escaping the scenario method itself becomes a single failing "composing the scenario"
step rather than taking the run down.

### The vocabulary

`Given` / `When` / `Then` take `Action`, `Func<Task>`, `Func<IStepContext, Task>`, and
value-returning forms that hand back a `Captured<T>`.

- `Then(text, () => value)` returns a `ValueExpectation<T>` — `ShouldBe`, `ShouldNotBe`,
  `ShouldBeNull`, `ShouldNotBeNull`, `ShouldSatisfy(predicate, "be positive")`, `ShouldMatch("NULL")`.
- `Then(() => account.Balance)` needs no text — `[CallerArgumentExpression]` renders it as
  "account.Balance should be 50".
- `Check(text, bool)` is the boolean step.
- `ThenRows(text, () => rows).KeyedBy("Id").ShouldMatch(...)` is set verification, through the same
  comparer a Gherkin `[SetVerification]` uses. See [Data Intensive Specifications](data-intensive-specifications.md).
- `.WithRows(objects)` renders an object's public properties as the step's input table, so a list of
  events reads as a list of event names.
- `Step(kind, text, (ctx, result, ct) => …)` is the raw escape hatch.

### One deliberate divergence from Gherkin

**A `Then` body that throws is an assertion failure, not a crash.** The step is marked failed with
the exception's message, and the scenario *continues* — so a run with three wrong assertions reports
three, not the first one. That is what anyone using Shouldly inside a `Then` wants.

`Given`/`When` that throw are critical exactly as in Gherkin. The generator still treats a throwing
`[Then]` *method* as critical, so the two surfaces disagree here on purpose.

Full detail, including which shapes the API handles awkwardly and what was deliberately not built:
[Code-first specifications](../code-first-specs.md).

## Specs from tests you already have

The other direction. Your xUnit v3 or TUnit tests stay where they are, keep running on their own
runner, and are *projected* as specifications — through marker comments and decorated helpers:

```csharp
[Fact]
public async Task deposit_increases_the_balance()
{
    // Given an account with a balance of 100
    var account = await OpenAccount(100);

    // When 50 is deposited
    await Deposit(account, 50);

    // Then the balance is 150
    account.Balance.ShouldBe(150);
}
```

A `[BobcatStep]` on a shared helper declares it once, so every test that calls it reads as that step.

Three things to know before you start:

- **xUnit v3 or TUnit only.** v2 has no adapter and is not waiting for one — its
  `BeforeAfterTestAttribute` cannot hand over a test result, which is the seam the adapter needs.
- **Use the shipped runner adapter** — `Bobcat.Xunit` or `Bobcat.TUnit`. A hand-rolled one reports
  a clean pass for red tests.
- **Declared is not executed.** A projected step describes what the test does; it does not make the
  test do it. The two can drift, and the docs are explicit about that limit.

The full treatment — including binding a projected test to an Event Model slice with `[BobcatSlice]`
and the spec-ownership manifest — is [Specs from tests you already have](../marker-steps.md).

## Where to go next

- Bulk data setup and set verification — [Data Intensive Specifications](data-intensive-specifications.md)
- Putting either style in a pipeline — [Integrating Bobcat with CI](continuous-integration.md)
