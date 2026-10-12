# Specifications with Code

**What you will build:** specifications written as ordinary tests -- your xUnit v3, TUnit, NUnit or
MSTest tests, rendered and reported as Given/When/Then specifications.

<!-- JEREMY -->
Bobcat specifications are code. There is no second file and no binding layer between the
specification and the test that executes it: the test *is* the specification, it keeps running on its
own runner, and Bobcat renders it as readable steps with their own verdicts.

## Three ways a step arrives

**A scenario vocabulary**, when your application is event sourced on the Critter Stack. Derive from
`CritterStackSpec` (or WolverineFx.Bobcat's `WolverineSpec`) and every member is a step:

```csharp
[BobcatFeature("Freeze Account")]
public class FreezeAccountSpecs(BankAccountHost app) : BankAccountSpec(app)
{
    [BobcatSpec(typeof(FreezeAccount), Domain = "Banking", TriggeredBy = "The fraud desk")]
    public async Task freezing_an_account_records_the_freeze()
    {
        var accountId = Guid.NewGuid();
        await GivenEvents<Account>(accountId, new AccountOpened(accountId, Guid.NewGuid(), "USD"));

        await WhenReceived(new FreezeAccount(accountId, "Suspected fraud"));

        ThenEmitted<AccountFrozen>();
        await ThenReadModel<Account>(accountId, Specify<Account>().With(x => x.IsFrozen, true));
    }
}
```

That one is from `samples/BankAccountES`, whose `BankAccountSpec` base derives from `CritterStackSpec`
and adds the application's own HTTP and message acts. `[BobcatSpec]` is the test, its slice binding
and its recording in one attribute.

**Decorated step helpers**, for any application. A step attribute on a shared helper declares it once,
so every test that calls it reads as that step:

```csharp
[Then("the response status is {status}")]
public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);
```

**Marker comments**, for a test you'd rather not touch:

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

What that buys you beyond a verdict: the run
[prints the specification it produced](../marker-steps.md#reading-the-specification-the-run-produced),
a step can report an [expected/actual cell](../marker-steps.md#checks-that-gather-instead-of-throwing)
without ending the test, and [tables and sets](../marker-steps.md#tables-and-sets-from-a-c-test)
grid up from a table literal in the test.

Three things to know before you start:

- **xUnit v3, TUnit, NUnit or MSTest (3.10+).** xUnit v2 has no adapter and is not waiting for
  one -- its `BeforeAfterTestAttribute` cannot hand over a test result, which is the seam the adapter
  needs.
- **Use the shipped runner adapter** -- `Bobcat.Xunit`, `Bobcat.TUnit`, `Bobcat.NUnit` or
  `Bobcat.MSTest`. A hand-rolled one reports a clean pass for red tests.
- **Declared is not executed.** A marker comment describes what the test does; it does not make the
  test do it. The two can drift, and the docs are explicit about that limit.

The full treatment -- including binding a specification to an Event Model slice with `[BobcatSpec]` or
`[BobcatSlice]` -- is [Writing Specifications](../marker-steps.md).

## Where to go next

- The whole Critter Stack vocabulary -- [Bobcat with Wolverine](../integrations/wolverine.md)
- Bulk data setup and set verification -- [Data Intensive Specifications](data-intensive-specifications.md)
- Putting a suite in a pipeline -- [Integrating Bobcat with CI](continuous-integration.md)
