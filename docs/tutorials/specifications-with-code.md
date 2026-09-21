# Specifications with Code

**What you will build:** specifications with no `.feature` files — your existing tests, rendered
and reported exactly like Gherkin ones.

Gherkin earns its keep when a non-developer reads the specs. When nobody does, its cost is a second
file, a binding layer, and a grammar to maintain.

Bobcat skips it by *projecting* the tests you already have: your xUnit v3 or TUnit tests stay where
they are, keep running on their own runner, and are rendered as specifications through selective
attributes and marker comments. There is no fluent Given/When/Then API to learn, and nothing to
rewrite.

## Specs from tests you already have

Your xUnit v3 or TUnit tests stay where they are, keep running on their own runner, and are
*projected* as specifications — through marker comments and decorated helpers:

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
