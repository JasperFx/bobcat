# Specifications with Code

**What you will build:** one business rule, specified three ways in plain C# against a plain .NET
class, with no Critter Stack and nothing hosted. You start from an xUnit test you could have written
yesterday, make it a specification without touching its body, give it a vocabulary so the next test
reads the same way, and then write the data-heavy cases as tables. By the end you have seen what the
run prints for a pass, a wrong value, a wrong row and a thrown exception.

Bobcat specifications are code. There is no second file and no binding layer between the
specification and the test that executes it: the test *is* the specification. It keeps running on
its own runner, in your IDE's test explorer, and Bobcat renders it as readable steps with a verdict
per step.

That is a deliberate compromise. A Gherkin or FitNesse tool lets a non-technical domain expert author
a specification directly, in a file of their own; a projected test is C#, so authoring stays with the
developers and the domain expert reviews the rendered specification instead. The tables below are the
other half of that compromise. A table in a test is an embedded markdown table, so for the
data-heavy cases a domain expert can read and edit the rows in a pull request without touching the
code around them, and the table in the test is the table in the specification.

The project setup is [Getting Started](../getting-started.md): the three packages, the runner
adapter, and `global.json`. This page assumes that and starts at the first test.

## The rule

A bank account that may not be overdrawn:

```csharp
public record Transaction(string Kind, decimal Amount);

public class Account
{
    public decimal Balance { get; private set; }
    public List<Transaction> Transactions { get; } = new();

    public void Deposit(decimal amount)
    {
        Balance += amount;
        Transactions.Add(new Transaction("Deposit", amount));
    }

    public bool TryWithdraw(decimal amount)
    {
        if (amount > Balance) return false;

        Balance -= amount;
        Transactions.Add(new Transaction("Withdrawal", amount));
        return true;
    }
}
```

## Step 1: a test you already have, as a specification

Here is the test as most teams would write it, with Shouldly:

```csharp
public class WithdrawalTests
{
    [Fact]
    public void a_withdrawal_larger_than_the_balance_is_refused()
    {
        var account = new Account();
        account.Deposit(100);

        var accepted = account.TryWithdraw(200);

        accepted.ShouldBeFalse();
        account.Balance.ShouldBe(100);
    }
}
```

Make it a specification without changing a line of its logic. Mark the class, and say what each
part of the body does in a comment that opens with `Given`, `When` or `Then`:

```csharp
using Bobcat.Xunit;

[BobcatFeature("Withdrawals")]
public class WithdrawalSpecs
{
    [Fact]
    public void a_withdrawal_larger_than_the_balance_is_refused()
    {
        // Given an account with a balance of 100
        var account = new Account();
        account.Deposit(100);

        // When 200 is withdrawn
        var accepted = account.TryWithdraw(200);

        // Then the withdrawal is refused and the balance is still 100
        accepted.ShouldBeFalse();
        account.Balance.ShouldBe(100);
    }
}
```

On xUnit `[BobcatFeature]` is all it takes: the `Bobcat.Xunit` package opens a scenario around every
test in the class and closes it with the verdict xUnit reported. On TUnit, NUnit and MSTest, put
`[BobcatScenario]` beside it. Run `dotnet test` in a terminal and the run prints the specification it
produced:

```
Feature: Withdrawals
════════════════════

  a withdrawal larger than the balance is refused OK
  ─────────────────────────────────────────────────
    ○ Given an account with a balance of 100
    ○ When  200 is withdrawn
    ○ Then  the withdrawal is refused and the balance is still 100
```

The `○` is honest. A **marker comment** declares a step; nothing observed it, so it has no duration
and no verdict of its own. The scenario's verdict is the test's. When the Shouldly assertion throws,
the scenario is red and the report carries the exception, but nothing narrower: no expected and
actual pair, and the second assertion is never reached. That is the limit of this style, and also
its whole appeal. Opting an existing suite in costs one attribute per class and some comments
([Marker comments](../marker-steps.md#marker-comments)).

::: tip Keep the comments, add the helpers
The two styles compose. A comment gives a test its narrative, and a decorated helper called
underneath it renders nested under that sentence, with its own timing and verdict
([They are different steps, and they nest](../marker-steps.md#they-are-different-steps-and-they-nest-issue-305)).
Most suites end up with both.
:::

## Step 2: a vocabulary

The second withdrawal spec should read like the first, and the tenth like the second. Move the
mechanics into **step helpers**: methods decorated with `[Given]`, `[When]` or `[Then]`, carrying a
sentence whose `{placeholders}` are filled from the arguments of those names:

```csharp
using Bobcat;

public class AccountSteps
{
    private readonly Account _account = new();
    private bool _accepted;

    [Given("an account with a balance of {balance}")]
    internal void AnAccountWithABalanceOf(decimal balance) => _account.Deposit(balance);

    [When("{amount} is withdrawn")]
    internal void IsWithdrawn(decimal amount) => _accepted = _account.TryWithdraw(amount);

    [Then("the withdrawal is {outcome}")]
    internal void TheWithdrawalIs(string outcome)
        => SpecAssert.Check("outcome", _accepted ? "accepted" : "refused", outcome);

    [Then("the balance is {balance}")]
    internal void TheBalanceIs(decimal balance) => SpecAssert.Check("balance", _account.Balance, balance);
}
```

The specification is now four calls:

```csharp
[BobcatFeature("Withdrawals")]
public class WithdrawalSpecs
{
    private readonly AccountSteps _steps = new();

    [Fact]
    public void a_withdrawal_larger_than_the_balance_is_refused()
    {
        _steps.AnAccountWithABalanceOf(100);
        _steps.IsWithdrawn(200);
        _steps.TheWithdrawalIs("refused");
        _steps.TheBalanceIs(100);
    }
}
```

Every call to a decorated helper is a recorded step: Bobcat's source generator intercepts the call at
build time, so there is no reflection and no registration. Two details carry weight:

- **The helpers are `internal`, not `private`.** The generated interceptor is an extension method,
  which cannot reach a private member. That one keyword is the whole adoption cost on an existing
  helper class ([BOBCAT039](../diagnostics.md#bobcat039)).
- **`SpecAssert.Check` records instead of throwing.** It writes an expected/actual cell onto the step
  in progress and returns, so the specification keeps going and reports every disagreement it has.
  The adapter fails the test once, at the end, with all of them
  ([Checks that gather instead of throwing](../marker-steps.md#checks-that-gather-instead-of-throwing)).

Now break the rule. Let `TryWithdraw` accept any amount, and the run prints:

```
  a withdrawal larger than the balance is refused FAILED
  ─────────────────────────────────────────────────────
    ✓ Given an account with a balance of 100
    ✓ When  200 is withdrawn
    ✗ Then  the withdrawal is expected 'refused', got 'accepted'
    ✗ And   the balance is expected '100', got '-100'
  Failed with Rights: 0, Wrongs: 2, Errors: 0
```

Both wrongs are there. A cell named for the placeholder it belongs to renders *in* the sentence, where
the value sits, and the test runner's own failure message says the same thing:

```
failed WithdrawalSpecs.a_withdrawal_larger_than_the_balance_is_refused
  SpecAssertionException : 2 specification steps failed:
    Then the withdrawal is refused => outcome: expected 'refused', got 'accepted'
    And the balance is 100 => balance: expected '100', got '-100'
```

A helper that throws is different. An exception is an **error**, not a wrong, and it stops the
scenario: the step is marked `!`, the exception is printed once at the bottom, and every step after
it renders as `○ … — not run` with its placeholders unfilled, because its arguments were never
evaluated ([Report everything, then stop](../marker-steps.md#gather-then-stop)).

## Step 3: the boundary as a decision table

The interesting cases of the overdraft rule are the ones at the edge: exactly the balance, a penny
over. Four specs of four lines each would say less than one table. A helper that takes a
`StepTable` receives the whole table from the call site, and `TableRunner.Run` calls a row method
once per row:

```csharp
[Then("withdrawals are decided as")]
internal Task WithdrawalsAreDecidedAs(StepTable table) => TableRunner.Run(this, nameof(decide), table);

private string decide(decimal balance, decimal amount)
{
    var account = new Account();
    account.Deposit(balance);
    return account.TryWithdraw(amount) ? "accepted" : "refused";
}
```

The `balance` and `amount` columns bind to the parameters of those names. The returned value is
compared against the one column no parameter names, `outcome`, so the table is a **decision table**
with a verdict per row:

```csharp
[Fact]
public Task the_overdraft_boundary() => _steps.WithdrawalsAreDecidedAs("""
    | balance | amount | outcome  |
    | 100     | 99.99  | accepted |
    | 100     | 100    | accepted |
    | 100     | 100.01 | refused  |
    """);
```

The table is pipe-delimited text, read by an implicit conversion to `StepTable`. A markdown table
pastes in unchanged, alignment row and all, so the table in the pull request and the table in the
test are the same text. With the rule broken the same way as before, the grid says which row:

```
    ✗ Then  withdrawals are decided as
╭───┬─────────┬────────┬───────────────────────────────────┬────────╮
│ # │ balance │ amount │ outcome                           │ Status │
├───┼─────────┼────────┼───────────────────────────────────┼────────┤
│ 1 │ 100     │ 99.99  │ accepted                          │   OK   │
│ 2 │ 100     │ 100    │ accepted                          │   OK   │
│ 3 │ 100     │ 100.01 │ expected 'refused', got 'accepted' │  FAIL  │
╰───┴─────────┴────────┴───────────────────────────────────┴────────╯
```

A row that throws is that row's failure and the remaining rows still run. `out` parameters are
output columns too, so one row method can judge several results
([Decision tables](data-intensive-specifications.md#decision-tables)).

## Step 4: the history as a set verification

The account's transaction list is a collection, and a collection claim is a **set verification**: the
expected rows are a table, the system's rows are matched against them, and every row gets its own
verdict.

```csharp
[Then("the transactions are")]
internal void TheTransactionsAre(StepTable expected)
    => SetVerificationComparer.Verify(_account.Transactions, expected, ordered: true);
```

```csharp
[Fact]
public void the_history_records_what_happened()
{
    _steps.AnAccountWithABalanceOf(100);
    _steps.IsWithdrawn(30);
    _steps.IsWithdrawn(200);

    _steps.TheTransactionsAre("""
        | Kind       | Amount |
        | Deposit    | 100    |
        | Withdrawal | 30     |
        """);
}
```

The refused withdrawal must leave no trace, and the table says so by not listing it. Suppose it did
leave one:

```
    ✗ Then  the transactions are
╭───┬────────────┬────────┬────────╮
│ # │ Kind       │ Amount │ Status │
├───┼────────────┼────────┼────────┤
│ 1 │ Deposit    │ 100    │   OK   │
│ 2 │ Withdrawal │ 30     │   OK   │
│ 3 │ Withdrawal │ 200    │ EXTRA  │
╰───┴────────────┴────────┴────────╯
```

`EXTRA` is a row the system produced that the specification does not describe. `MISSING` is a row the
specification describes that the system never produced, shown with the values it expected. `FAIL` is a
matched row with a wrong cell, and `ORDER` is a row in the wrong place when order was part of the
claim. Name `keyColumns:` to say which columns identify a row, and leave `ordered:` out when order
is not the claim ([Set verification](data-intensive-specifications.md#set-verification)).

## Step 5: one specification per example

A decision table judges many rows inside one specification, with one verdict. When each example is
something a person may want to run, see fail or track on its own, make each row its own
specification instead. `[BobcatTheory]` is xUnit's `[Theory]` plus the scenario bracket, and
`[BobcatExamples]` writes its rows as the same table:

```csharp
[BobcatTheory]
[BobcatExamples("""
    | balance | amount | outcome  |
    | 100     | 100    | accepted |
    | 100     | 100.01 | refused  |
    """)]
public void a_withdrawal_is_decided(decimal balance, decimal amount, string outcome)
{
    _steps.AnAccountWithABalanceOf(balance);
    _steps.IsWithdrawn(amount);
    _steps.TheWithdrawalIs(outcome);
}
```

Each row runs as its own test with its own identity,
`Withdrawals/a withdrawal is decided (balance: 100, amount: 100.01, outcome: refused)`, and its own
rendered steps. Columns bind to parameters by name, and because the table is a constant, the build
checks it: a column naming no parameter or a cell the parameter's type cannot read is a compile
error rather than a surprise at run time
([A theory's examples as a table](../marker-steps.md#bobcatexamples)).

## Reading and running

- **The run prints its specifications** when a terminal is attached and nothing is listening on the
  monitor wire. `BOBCAT_SPEC_CONSOLE=1` prints anyway, into CI's captured output;
  `BOBCAT_SPEC_CONSOLE_FILE=spec.txt` writes the report to a file instead
  ([Reading the specification the run produced](../marker-steps.md#reading-the-specification-the-run-produced)).
- **`bobcat run`** renders the specification tree in the terminal, and **`bobcat preview`** shows
  every specification without executing anything, because the steps are known at build time
  ([The `bobcat` tool](../bobcat-tool.md#run-preview-pick-watch)).
- **Your IDE's test explorer** shows each specification as the ordinary test it is, so you run and
  debug it there ([Integrating Bobcat with Your IDE](ide-integration.md)).
- **The assertions you already wrote can be steps too.** With
  `<BobcatProjectAssertions>true</BobcatProjectAssertions>`, a statement-level Shouldly call inside a
  spec renders as a step with a cell, and a run of consecutive assertions is all evaluated before the
  next action ([Projecting the assertions you already wrote](../marker-steps.md#projecting-the-assertions-you-already-wrote-opt-in)).

## Three things to know before you start

- **xUnit v3, TUnit, NUnit or MSTest (3.10+).** xUnit v2 has no adapter and is not waiting for one.
  Its `BeforeAfterTestAttribute` cannot hand over a test result, which is the seam the adapter needs
  ([xUnit v2 is not supported](../xunit.md#xunit-v2-is-not-supported-and-is-not-waiting-for-an-adapter)).
- **Use the shipped runner adapter**, `Bobcat.Xunit`, `Bobcat.TUnit`, `Bobcat.NUnit` or
  `Bobcat.MSTest`. Marten's hand-rolled one never set the verdict, so every scenario it published was
  a clean pass, red tests included ([What you have to add](../marker-steps.md#what-you-have-to-add)).
- **Declared is not executed.** A marker comment describes what the test does; it does not make the
  test do it. The two can drift, and a comment can never carry a cell. When a step is worth a
  verdict of its own, it is worth a helper ([Declared is not executed](../marker-steps.md#declared-is-not-executed)).

## Where this goes next

- **On the Critter Stack the vocabulary is already written.** `CritterStackSpec`, `WolverineSpec` and
  `WolverineHttpSpec` in WolverineFx.Bobcat carry `GivenEvents`, `WhenReceived`, `WhenPosted`,
  `ThenEvents` and the rest, so a Wolverine, Marten, Polecat or Fisher application starts at Step 2
  with nothing to write ([Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat), in
  the Wolverine docs).
- **A specification can name the slice it specifies.** `[BobcatSpec(typeof(WithdrawFunds))]` is the
  `[Fact]`, the scenario bracket and the Event Model binding in one attribute, which is what lets
  `bobcat slice-coverage` say which slices have no spec
  ([One attribute instead of three](../marker-steps.md#bobcatspec),
  [Event Modeling and Spec Driven Development](event-modeling.md)).
- **The data-heavy forms in full:** tables of objects as input, decision tables with `out`
  parameters, one object against one row, partial objects, and what a cell may say
  ([Data Intensive Specifications](data-intensive-specifications.md),
  [Expected Objects](../expected-objects.md)).
- **Everything else a step can do** is in [Writing Specifications](../marker-steps.md).
- Putting a suite in a pipeline is [Integrating Bobcat with CI](continuous-integration.md).
