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
adapter, and `global.json`. This page assumes that and starts at the first test. Every sample on it
is compiled and run in Bobcat's own repository, in
[`src/Bobcat.Xunit.Samples`](https://github.com/JasperFx/bobcat/tree/main/src/Bobcat.Xunit.Samples),
and every output block is what that run printed.

## The rule

A bank account that may not be overdrawn:

<<< ../../src/Bobcat.Xunit.Samples/Application/Account.cs#account{cs}

## Step 1: a test you already have, as a specification

Here is the test as most teams would write it, with Shouldly:

<<< ../../src/Bobcat.Xunit.Samples/Specs/WithdrawalSpecs.cs#withdrawal-as-written{cs}

Make it a specification without changing a line of its logic. Mark the class
`[BobcatFeature("Withdrawals")]`, and say what each part of the body does in a comment that opens
with `Given`, `When` or `Then`:

<<< ../../src/Bobcat.Xunit.Samples/Specs/WithdrawalSpecs.cs#withdrawal-narrated{cs}

On xUnit, `[BobcatFeature]` is all it takes: the `Bobcat.Xunit` package opens a scenario around every
test in the class and closes it with the verdict xUnit reported. On TUnit, NUnit and MSTest, put
`[BobcatScenario]` beside it. (The sample project references Bobcat by project rather than by
package, so it states `[BobcatScenario]` itself.) Run `dotnet test` in a terminal and the run prints
the specification it produced:

```
Feature: Withdrawals
═════════════════════

  a withdrawal larger than the balance is refused narrated OK
  ────────────────────────────────────────────────────────────
    · Given an account with a balance of 100 — declared
    · When  200 is withdrawn — declared
    ✓ Then  the withdrawal is refused and the balance is still 100 (3ms)
      ✓ Then  accepted should be false (1ms)
      ✓ And   account.Balance should be 100 (2ms)
```

The `declared` is honest. A **marker comment** declares a step; nothing observed the Given or the
When, so they have no duration and no verdict of their own. The Then is different only because the
sample project opts into projecting Shouldly assertions as steps, so the two calls under the comment
rendered beneath it with their own verdicts. Without that opt-in the Then would read `declared` as
well, and when a Shouldly assertion threw, the scenario would be red with the exception and nothing
narrower: no expected and actual pair, and the second assertion never reached. That is the limit of
this style, and also its whole appeal. Opting an existing suite in costs one attribute per class and
some comments ([Marker comments](../marker-steps.md#marker-comments),
[Projecting the assertions you already wrote](../marker-steps.md#projecting-the-assertions-you-already-wrote-opt-in)).

::: tip Keep the comments, add the helpers
The two styles compose. A comment gives a test its narrative, and a decorated helper called
underneath it renders nested under that sentence, with its own timing and verdict
([They are different steps, and they nest](../marker-steps.md#they-are-different-steps-and-they-nest-issue-305)).
Most suites end up with both.
:::

## Step 2: a vocabulary

The second withdrawal spec should read like the first, and the tenth like the second. Move the
mechanics into **step helpers** on a class of their own: methods decorated with `[Given]`, `[When]`
or `[Then]`, carrying a sentence whose `{placeholders}` are filled from the arguments of those names:

<<< ../../src/Bobcat.Xunit.Samples/Grammars/AccountSteps.cs#account-steps{cs}

The specification is now four calls:

<<< ../../src/Bobcat.Xunit.Samples/Specs/WithdrawalSpecs.cs#withdrawal-with-steps{cs}

Every call to a decorated helper is a recorded step: Bobcat's source generator intercepts the call at
build time, so there is no reflection and no registration. Two details carry weight:

- **The helpers are `internal`, not `private`.** The generated interceptor is an extension method,
  which cannot reach a private member. That one keyword is the whole adoption cost on an existing
  helper class ([BOBCAT039](../diagnostics.md#bobcat039)).
- **`SpecAssert.Check` records instead of throwing.** It writes an expected/actual cell onto the step
  in progress and returns, so the specification keeps going and reports every disagreement it has.
  The adapter fails the test once, at the end, with all of them
  ([Checks that gather instead of throwing](../marker-steps.md#checks-that-gather-instead-of-throwing)).

Now write a specification whose expectations are wrong. A withdrawal of 30 from 100 is accepted and
leaves 70, and this spec says otherwise, twice:

<<< ../../src/Bobcat.Xunit.Samples/Specs/WithdrawalSpecs.cs#withdrawal-wrong-expectation{cs}

```
  a withdrawal within the balance is refused FAILED
  ────────────────────────────────────────────────────
    ✓ Given an account with a balance of 100
    ✓ When  30 is withdrawn
    ✗ Then  the withdrawal is expected 'refused', got 'accepted' (1ms)
    ✗ And   the balance is expected '100', got '70' (1ms)
  Failed with Rights: 2, Wrongs: 2, Errors: 0
```

Both wrongs are there. A cell named for the placeholder it belongs to renders *in* the sentence, where
the value sits, and the test runner's own failure message says the same thing:

```
failed Bobcat.Xunit.Samples.Specs.WithdrawalSpecs.a_withdrawal_within_the_balance_is_refused (38ms)
  Xunit.MicrosoftTestingPlatform.XunitException: Bobcat.Engine.SpecAssertionException : 2 specification steps failed:
    Then the withdrawal is refused => outcome: expected 'refused', got 'accepted'
    And the balance is 100 => balance: expected '100', got '70'
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

<<< ../../src/Bobcat.Xunit.Samples/Grammars/AccountSteps.cs#withdrawals-decided-as{cs}

The `balance` and `amount` columns bind to the parameters of those names. The returned value is
compared against the one column no parameter names, `outcome`, so the table is a **decision table**
with a verdict per row:

<<< ../../src/Bobcat.Xunit.Samples/Specs/WithdrawalSpecs.cs#overdraft-boundary{cs}

The table is pipe-delimited text, read by an implicit conversion to `StepTable`. A markdown table
pastes in unchanged, alignment row and all, so the table in the pull request and the table in the
test are the same text. Give one row the wrong answer and the grid says which:

<<< ../../src/Bobcat.Xunit.Samples/Specs/WithdrawalSpecs.cs#overdraft-boundary-wrong-row{cs}

```
  the overdraft boundary with a wrong row FAILED
  ─────────────────────────────────────────────────
    ✗ Then  withdrawals are decided as (4ms)
╭───┬─────────┬────────┬────────────────────────────────────┬─────────╮
│ # │ balance │ amount │ outcome                            │ Verdict │
├───┼─────────┼────────┼────────────────────────────────────┼─────────┤
│ 1 │ 100     │ 99.99  │ accepted                           │   OK    │
│ 2 │ 100     │ 100    │ expected 'refused', got 'accepted' │  FAIL   │
│ 3 │ 100     │ 100.01 │ refused                            │   OK    │
╰───┴─────────┴────────┴────────────────────────────────────┴─────────╯
  Failed with Rights: 2, Wrongs: 1, Errors: 0
```

A row that throws is that row's failure and the remaining rows still run. `out` parameters are
output columns too, so one row method can judge several results
([Decision tables](data-intensive-specifications.md#decision-tables)).

## Step 4: the history as a set verification

The account's transaction list is a collection, and a collection claim is a **set verification**: the
expected rows are a table, the system's rows are matched against them, and every row gets its own
verdict.

<<< ../../src/Bobcat.Xunit.Samples/Grammars/AccountSteps.cs#transactions-are{cs}

<<< ../../src/Bobcat.Xunit.Samples/Specs/WithdrawalSpecs.cs#history{cs}

The refused withdrawal must leave no trace, and the table says so by not listing it. Now a
specification that forgets a withdrawal that did happen:

<<< ../../src/Bobcat.Xunit.Samples/Specs/WithdrawalSpecs.cs#history-extra-row{cs}

```
  the history the specification did not expect FAILED
  ──────────────────────────────────────────────────────
    ✓ Given an account with a balance of 100
    ✓ When  30 is withdrawn
    ✗ Then  the transactions are (4ms)
╭───┬────────────┬────────┬─────────╮
│ # │ Kind       │ Amount │ Verdict │
├───┼────────────┼────────┼─────────┤
│ 1 │ Deposit    │ 100    │   OK    │
│ 2 │ Withdrawal │ 30     │  EXTRA  │
╰───┴────────────┴────────┴─────────╯
  Failed with Rights: 4, Wrongs: 1, Errors: 0
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

<<< ../../src/Bobcat.Xunit.Samples/Specs/WithdrawalSpecs.cs#withdrawal-examples{cs}

Each row runs as its own test with its own identity and its own rendered steps:

```
  a withdrawal is decided (balance: 100, amount: 100, outcome: accepted) OK
  ────────────────────────────────────────────────────────────
    ✓ Given an account with a balance of 100
    ✓ When  100 is withdrawn
    ✓ Then  the withdrawal is accepted (1ms)
```

Columns bind to parameters by name, and because the table is a constant, the build checks it: a
column naming no parameter or a cell the parameter's type cannot read is a compile error rather
than a surprise at run time ([A theory's examples as a table](../marker-steps.md#bobcatexamples)).

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
  with nothing to write ([Testing with Bobcat](https://wolverinefx.net/guide/testing#with-wolverinefx-bobcat), in
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
