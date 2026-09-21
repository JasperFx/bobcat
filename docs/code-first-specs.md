# Code-First Specifications

Specifications written in C#, with no `.feature` file and no generator. They run on the same
engine, render the same report, supervise the same way, and appear on the Event Model the same way
as Gherkin specs — the only thing that changes is where the scenario is written.

Reach for this when nobody outside the team is reading the specs. Gherkin earns its second file and
its binding layer when a non-developer reads it; when nobody does, this is the cheaper shape. If
you have an existing suite you would rather not rewrite at all, see
[Specs From Existing Tests](marker-steps.md) instead — that is a third, different answer.

## Your first specification

```csharp
public class OrderSagaSpecs : Specification              // feature title: "Order Saga"
{
    [Scenario("Starting an order")]
    public void starting_an_order()
    {
        var orderId = Guid.NewGuid().ToString();

        var run = When("StartOrder is received", ctx => ctx.InvokeMessageAndWaitAsync(new StartOrder(orderId)))
            .WithRows(new StartOrder(orderId));

        Then("the Order saga document", ctx => load(ctx, orderId)).ShouldNotBeNull();
        Then("the OrderTimeout scheduled by the saga", () => run.Value.Scheduled.SingleMessage<OrderTimeout>().Id)
            .ShouldBe(orderId);
    }
}
```

A `Specification` **is** a `Fixture` — one fresh instance per scenario, the same `Context`, the same
lifecycle hooks (`BeforeEach`/`AfterEach`, static `BeforeAll`/`AfterAll`, with or without an `Async`
suffix), the same recovery-hint attributes.

The feature title comes from the class name minus a `Specification`/`Specs`/`Spec`/`Fixture`
suffix; `[FixtureTitle]` overrides it. A scenario's title comes from `[Scenario("…")]`, or from the
method name with underscores read as spaces.

## Registering it

```csharp
runner.AddSpecification<OrderSagaSpecs>();          // one
runner.ScanForSpecifications(assembly);             // all of them
```

Under `Bobcat.Mtp` the generated entry point scans for you and neither line is needed — see
[Integrating Bobcat Gherkin](integrating-gherkin.md#dotnet-test).

## Compose, then execute

**This is the one thing to understand, and everything awkward follows from it.**

A scenario method does not run your test. It runs at plan-build time and **declares** steps, which
the engine then executes with the same timeouts, continuation rules and observers every other
scenario gets.

Two consequences you will hit in the first hour:

**The method body cannot `await` a step's outcome.** A value-returning `Given`/`When` hands back a
`Captured<T>`, read as `.Value` *inside a later step*:

```csharp
var run = When("the command is sent", ctx => ctx.InvokeMessageAndWaitAsync(cmd));

// ✗ run.Value here — nothing has executed yet
Then("the balance", () => run.Value.Balance).ShouldBe(50);   // ✓ read inside a step
```

Reading a `Captured` too early throws with an explanation rather than a null reference.

**Anything needing the step context belongs in a step body**, not in the method around them. Prefer
the `ctx =>` overloads over `Context!` from the fixture — they read better and cannot be used too
early.

An exception escaping the scenario method itself becomes a single failing "composing the scenario"
step rather than taking the run down.

::: warning A value-returning body is awaited inside its own step
`Given("a waiter", () => Handler.WaitForNextMessage())` infers `Func<Task<T>>` and **awaits the
waiter inside the Given** — which is a hang, not a handle. If you want a task to run alongside
later steps, put it in a field rather than returning it from a step.
:::

## The vocabulary

### Steps

`Given`, `When` and `Then` each take `Action`, `Func<Task>` and `Func<IStepContext, Task>`.
`Given` and `When` also take value-returning forms — `Func<T>`, `Func<Task<T>>`,
`Func<IStepContext, Task<T>>` — which return a `Captured<T>`.

`Step(kind, text, (ctx, result, ct) => …)` is the raw escape hatch when you need the `StepResult`
itself. `StepKind` lives in `Bobcat.Engine`.

### Asserting a value

`Then<T>(text, …)` returns a `ValueExpectation<T>`:

```csharp
Then("the balance", () => account.Balance).ShouldBe(50);
Then("the closing reason", () => account.Reason).ShouldNotBeNull();
Then("the balance", () => account.Balance).ShouldSatisfy(b => b > 0, "be positive");
Then("the closed date", () => account.ClosedOn).ShouldMatch("NULL");
```

`ShouldBe`, `ShouldNotBe`, `ShouldBeNull`, `ShouldNotBeNull`, `ShouldSatisfy(predicate,
description)`, and `ShouldMatch(text)` — the last running through the same cell checker a Gherkin
table uses, so `"NULL"`, `"EMPTY"` and friends mean what they mean everywhere else.

There is a text-free form for the common case:

```csharp
Then(() => account.Balance).ShouldBe(50);      // renders "account.Balance should be 50"
```

`Check(text, () => predicate)` is the boolean step.

### Asserting a set

```csharp
ThenRows("the open orders", ctx => query(ctx))
    .KeyedBy("Id")
    .ShouldMatch(new { Id = orderId, Completed = false });

ThenRows("the cancelled orders", ctx => cancelled(ctx)).ShouldBeEmpty();
```

Expected rows are any objects whose public properties name the columns — **anonymous types read
best**. `KeyedBy` matches rows by key rather than by order, and the report shows missing rows,
extra rows and per-cell disagreements, exactly as the Gherkin `[SetVerification]` does, because it
is the same comparer.

### Showing your inputs

```csharp
Given("the streams", () => seed()).WithRows(streams);
When("StartOrder is received", …).WithRows(new StartOrder(orderId));
```

`WithRows` renders the objects' public properties as the step's input table, so a command shows as
a row and an event stream shows as a list of event names. A marker record with no properties, or
rows of mixed types, gets a `type` column. It works on both `StepHandle` and `Captured<T>`.

## A `Then` that throws is an assertion failure

**The one deliberate divergence from Gherkin.** A `Then` body that throws marks that step failed
with the exception's message and the scenario *continues*, so a run with three wrong assertions
reports three rather than the first. That is what anyone using Shouldly inside a `Then` wants.

`Given` and `When` that throw are critical exactly as in Gherkin, and
`SpecCriticalException`/`SpecCatastrophicException` mean what they mean anywhere.

One honest consequence: a genuinely broken `Then` — a null reference while *computing* the value —
is reported as `failed` rather than `error`. The message names it, so it is not hidden, but a
policy keying off failed-vs-error sees a disagreement. That is the price of gathering, and it is
the right price for a `Then`.

::: tip The generator disagrees on purpose
A throwing `[Then]` *method* on a Gherkin fixture is still critical. The two surfaces differ here
deliberately, and that has not been reconciled.
:::

## Tags

```csharp
[Scenario("Starting an order", Tags = ["retry(2)", "isolated", "slice:StartOrder"])]
```

Tags are the Gherkin vocabulary verbatim — `retry(N)`, `isolated`, `timeout(60)` — which is the
point: the whole resilience layer reads them, and a code-first scenario is filtered, retried and
isolated by exactly the same machinery. There is no IntelliSense for them.

## Borrowing a fixture's steps

```csharp
protected TFixture Host<TFixture>() where TFixture : Fixture, new();
```

A specification can host a fixture and call its methods inside step bodies. That covers sharing a
vocabulary between the two styles without a second text-matching engine.

## With the Critter Stack

`CritterStackFixture` carries typed steps that a `Specification` borrows through `Host<TFixture>()`:

| | |
|---|---|
| `GivenEvents<T>` / `GivenNoEvents<T>` | arrange a stream, or assert it starts empty |
| `WhenCommand<T>` | tracked-session dispatch, outcome captured |
| `WhenTracked(() => …)` | any act — typically an Alba HTTP call — inside the tracked session |
| `ThenEvents(…)` / `ThenNoEvents()` | the events the act appended |
| `ThenValidationFails(text)` / `ThenCommandRefused()` | the two refusal shapes |
| `ThenDocument<T>` | a read model, with a projection wait |
| `ThenMessagesSent<T>()` | what the act put on the bus |

See [Composing Grammar Modules](composing-grammars.md) for the grammar side of the same vocabulary.

## Writing step text that reads well

The report renders your text followed by the expectation, so write the text as a **noun phrase**:

```csharp
Then("the order is completed", () => order.Completed).ShouldBe(false);   // "…is completed should be false"
Then("whether the order is completed", () => order.Completed).ShouldBe(false);   // ✓
```

## On the Event Model

Code-first scenarios fold into the same slice dictionary `.feature` files feed. Slice and domain
come from tags (`slice:X`, `domain:Y`); roles come from the typed-step convention in the method
body — `WhenCommand<T>` gives the aggregate and command, `ThenEvents` the events, `ThenDocument<T>`
the read model, `ThenMessagesSent<T>` the message — gated on the target being declared on a
`Fixture` subclass, so an unrelated method never stamps a phantom role.

An empty `[Scenario]` method is the pending-specification hotspot.

Unlike the Gherkin HTTP lane, a code-first specification stamps **no trigger kind**: it records the
roles a scenario resolved, not the grammar step that resolved them.

## See also

- [Specifications with Code](tutorials/specifications-with-code.md) — the tutorial, and how this compares to the projected lane
- [Specs From Existing Tests](marker-steps.md) — the other way to avoid Gherkin
- [Composing Grammar Modules](composing-grammars.md) — the shipped Critter Stack vocabulary
