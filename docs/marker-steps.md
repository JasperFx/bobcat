# Specs from tests you already have

Issue #110. The other two authoring styles ask you to write a specification: a `.feature` file
bound to a fixture. This one asks for
almost nothing. You point Bobcat at tests that already exist, in whatever runner they already use,
and they start reporting themselves as specifications — ordered steps with their own verdicts, a
`{Feature}/{Scenario}` identity, [a rendered specification on the console](#reading-the-specification-the-run-produced),
tables and sets that grid up the same way a `.feature` file's do, and live progress
[on the wire a run publishes](monitor-design.md).

The constraint that shaped it: a large existing suite has to be able to adopt this **one class at
a time**, without a base class, a signature change, or a rewrite. Anything more expensive than
that and the answer is always "not this quarter".

Marten's `DaemonTests` is the first real subject ([JasperFx/marten#5363](https://github.com/JasperFx/marten/pull/5363)) --
eighteen tests, no test body edited.

## Two ways in, and they compose

**Marker comments** declare the steps of one test:

```csharp
using Bobcat.Xunit;      // or Bobcat.TUnit — the runner adapter, see below

[BobcatFeature("Async daemon"), BobcatScenario]
public class when_the_daemon_catches_up : DaemonContext
{
    [Fact]
    public async Task the_projection_catches_up()
    {
        // Given the events are published
        await PublishEvents();

        // the daemon polls on its own schedule, which is why the wait below exists
        await WaitForNonStaleAsync();

        // When the projection daemon is running
        using var daemon = await StartDaemon();

        // Then every expected aggregate matches
        await CheckAllExpectedAggregatesAgainstActuals();
    }
}
```

Three of those comments are steps and one is a comment. A marker is a `//` comment opening with
`Given`, `When`, `Then`, `And` or `But` as a whole word — everything else stays invisible, which
is the property that makes this usable on a real suite full of explanatory comments.

`[BobcatFeature]` names the feature and `[BobcatScenario]` comes from the
[runner adapter](#the-runner-adapter). **Both are needed.** The comments alone register declared
steps at compile time, but without the adapter no scenario is ever opened, so nothing publishes —
you get the half of the feature that is invisible.

**A step attribute on a shared helper** goes the other way. Decorate it once and *every* test that
already calls it renders that step:

```csharp
[Given("the events are published on {threads} threads")]
internal Task PublishMultiThreaded(int threads) => …
```

`{threads}` is filled in from the call site, so one attribute renders `PublishMultiThreaded(3)` as
"Given the events are published on 3 threads".

The two compose. Comments give a test its narrative; decorated helpers give real per-step timing
across every test in the suite that touches them.

### One attribute family, both lanes

`[Given]`, `[When]`, `[Then]`, `[Check]` and the keywordless `[Step]` all derive from one
`StepAttribute`, and **every one of them works in both authoring styles**: matched against a
`.feature` file's step text on a fixture, and intercepted at the call site when an ordinary xUnit or
TUnit test calls the method directly. There used to be a second vocabulary for the second case —
`[BobcatStep]` — and the split had no reason behind it: a step's text and keyword are the same facts
whichever way the step is reached.

`[BobcatStep]` is still accepted and still means the same thing. Prefer the keyword attributes in new
code; its `Keyword = "Given"` property is what `[Given]` says in one word.

Each expression may be written in **either** of two syntaxes:

| | |
|---|---|
| a **Cucumber expression** | captures by *type* and position — `"the left operand is {int}"`, plus `{string}`, `{word}`, the Event Modeling type words (`{aggregate}`, `{command}`, `{event}`, …), optional text and raw regex |
| a **named template** | Storyteller's `[FormatAs]` syntax, capturing by *parameter name* — `"Adding {x} to {y} should equal {sum}"` over `double Adding(double x, double y, double sum)` |

Which one is in force is decided **per placeholder**, built-in word first: `{int}` stays a Cucumber
capture even on a method with a parameter called `int`, so nothing that compiled before means
anything different. A placeholder that is not a built-in type word and does name a parameter is the
named form. The two mix freely in one expression — `"the {aggregate} has {count} events"` is a
natural thing to write and there is no ambiguity in it, because each placeholder is resolved on its
own.

In the named form the parameter's own declared type decides how a cell is read, so the step text says
what the value *is* rather than merely what type it has — and the same string renders the sentence
when the method is called from C#.

**Keywordless is a real choice, not a default.** Storyteller sentences and Gauge steps read as prose,
and "Multiply by 3 then add 4" is not a Given, a When or a Then:

```csharp
[Step("Multiply by {multiplier} then add {delta}")]
internal void MultiplyThenAdd(int multiplier, int delta) { … }
```

In the Gherkin lane a keywordless step matches under *any* keyword — the rule `[TableGrammar]` has
always followed — so the feature file decides, which is where that decision belongs. An empty keyword
is a third state distinct from "unknown": it renders no label, and it is never promoted to `And`,
because `And` is a word the author never wrote.

### A placeholder binds from the value, not from the syntax (issue #339)

A literal argument is substituted at build time. Everything else binds **at execution time**, from
the value the helper was actually called with — which matters because a typed vocabulary has
almost no literals in it:

```csharp
[Given("{aggregate} \"{id}\" has already recorded these events")]
internal Task GivenEvents(Type aggregate, Guid id) => …

[When("{command} is posted to \"{route}\"")]
internal Task WhenPosted(object command, string route) => …
```

```
Given Appointment "8f1c…" has already recorded these events
When ConfirmAppointment is posted to "/api/scheduling/confirmappointment"
Then AppointmentConfirmed is emitted
Then the response is 404
```

Before this, only the literals bound: the route rendered and `{command}` did not, because the
argument is `new ConfirmAppointment(id)`. Measured on a real projected suite, **73 of 95** step
texts carried a raw placeholder and the canvas showed `{event} is emitted` sixteen times — the
more faithfully the vocabulary was built, the less of it rendered. It also closed a loop: a generic
helper cannot be intercepted at all, so `GivenEvents<Appointment>(id)` has to become
`GivenEvents(typeof(Appointment), id)`, and `typeof(Appointment)` was precisely the argument shape
that could not bind.

How a value reads:

| value | renders as |
|---|---|
| `Type` | its short name — `Appointment` |
| `string`, number, `bool`, `Guid`, enum, date/time | the value, culture-invariant |
| a sequence | its items, comma-joined, capped at five |
| anything else | its **type** name — `new ConfirmAppointment(id)` is `ConfirmAppointment` |
| `null`, an empty string, an empty sequence | nothing: `{name}` stays as written |

A step's text is a sentence on a canvas, which is why an object renders as its type rather than its
`ToString()` — the data belongs in a table, not in the prose. And a value with nothing to say
leaves the placeholder standing, because a reader can see that something did not resolve, where a
blank or the word "null" would be believed.

**A placeholder that names no parameter is now a warning** — `BOBCAT027`, once per helper. It used
to look identical to the placeholders that were merely deferred, so a template typo rendered as
`{thread}` forever with nothing reported.

### They are different steps, and they nest (issue #305)

When a decorated helper is called inside a comment-declared region, **both** describe the same
stretch of the test — and the answer is not a precedence rule, because they are not the same kind
of claim. The comment is the author's sentence about *this* test. The attribute reports an
*operation*, wherever it is called from. So the comment is the row, and the helper renders
underneath it with its own keyword and its own verdict:

```
Given the events are published                    52ms
    Given the events are published on 3 threads   40ms
    Given the events are flushed                  12ms
When the projection daemon is running             declared
```

Neither keyword overrides the other, because they never occupy the same line. A helper called
where no comment has been written yet — before the first marker, or from a method that is not a
test — belongs to no sentence and renders on its own.

### What a declared step can say about itself (issue #304)

A declared step reports **the work observed inside it**: the steps that ran under it, their
verdicts, and the sum of their durations. It reports nothing else, and a region with no decorated
helper inside it stays blank — marked `declared`, with no duration and no verdict. Nothing
observed it, and a number borrowed from its neighbours would be exactly the inference this feature
promises not to make.

The attribution is decided **by the generator, from the call site's line**, against the comments
in the same method. Not from a stack trace, not from a clock: an interceptor is generated per call
site, so at build time which sentence a call sits under is an exact fact, and at runtime it would
be a guess. An index that does not match the comments actually registered — a stale `obj/`, a
class the feature attribute never marked — attributes to nothing rather than to the wrong
sentence.

## What you have to add

| | |
|---|---|
| Packages | `Bobcat`, `Bobcat.Generators`, and the adapter for your runner |
| A runner adapter | `Bobcat.Xunit` or `Bobcat.TUnit` |

That is the whole list. `Bobcat.Generators` brings the interceptor opt-in with it, so there is no
csproj line to remember -- and no sibling project that silently needed the same one.

Marker comments reach the runtime through a module initializer, which is why opting in really is
only comments -- nothing in a test body could have been made to carry them, and an assembly with no
marked class gains no initializer and no startup cost.

### A decorated helper cannot be `protected`

A C# interceptor must be an extension method, and an extension method cannot see a `protected`
member. So every decorated helper has to be `internal` or `public`. This is a language rule rather
than a Bobcat design choice, and it is the one real cost of the attribute half — widening eight
helpers on `DaemonContext` is exactly what Marten's PR did. Marker comments have no such
constraint.

### The runner adapter

Add the package for your runner and put `[BobcatScenario]` on the test class:

```csharp
using Bobcat.Xunit;      // or Bobcat.TUnit

[BobcatFeature("Async daemon"), BobcatScenario]
public class DaemonSpecs
{
    [Fact]
    public async Task the_daemon_catches_up()
    {
        // Given events are published
        ...
    }
}
```

It works on a single method too. Both packages open a scenario around each test and close it with
**the verdict the runner reported** -- a failing test is published as a failure, and a skipped one
is withdrawn rather than reported as anything.

Bobcat deliberately shipped no adapter at first, on the theory that forty lines were cheaper than
guessing at a runner. That was wrong, and the reason is worth stating: those forty lines carry four things only
Bobcat knows, and getting any of them wrong leaves a green suite looking exactly like a correct one.
Marten's hand-rolled copy -- the one this page used to invite you to paste -- got all four wrong.
It never set the verdict, so every scenario it ever published was a `CleanPass`. It minted its own
run id and dropped `BOBCAT_RUN_TAG`, so its evidence could not be attributed to whatever asked for
the run. It published a `RunStarted` it might not own, and never a `RunFinished`. And its
interceptor opt-in was a csproj line that a sibling project also needed, where forgetting it is a
`CS9137` build failure rather than a missing feature.

**xUnit v2 has no adapter and is not simply waiting for one.** v2's `BeforeAfterTestAttribute` is
`Before(MethodInfo)` / `After(MethodInfo)` with no test context anywhere, so there is no verdict to
read at that point at all -- reporting one would mean replacing the test framework rather than
hooking it. v3 and TUnit both hand the result over at the end of a test, which is what makes their
adapters small.

## Reading the specification the run produced

Everything a projected test records leaves the process over the monitor wire, and for a long time
that was the *only* way out — so the rendering, which is the whole point of projecting tests in the
first place, was the one thing a consumer could not see without a console running somewhere.

**A projected run now prints its specifications itself**, grouped by feature, in the same Spectre
output a `.feature` suite gets:

```
Feature: Calculator
════════════════════

  arithmetic holds OK
    ✓ Start with the number 5
    ✓ Multiply by 3 then add 4
    ✓ The number should now be 17
        ✓ number: 17

  asserting values FAILED
    ✓ For X=2 and Y=3, the Sum should be 5 and the Product should be 6
    ✗ For X=4 and Y=4, the Sum should be 6 and the Product should be 8
        ✗ Sum: expected '6', got '8'
        ✓ Product: 8
    ✓ For X=1 and Y=1, the Sum should be 2 and the Product should be 1

2 specification(s) — not green
```

It is on **when a terminal is attached and nothing is listening on the wire** — precisely the case
where the run would otherwise say nothing at all. Those two conditions are what keep it from being a
nuisance: a redirected stream belongs to whoever redirected it (a test platform, a CI runner, a
pipe), and a console on the wire already renders these specifications better than Spectre can, so
printing them twice would read as two reports of one run.

| | |
|---|---|
| `BOBCAT_SPEC_CONSOLE=1` | print anyway — into a captured stream, with a console listening |
| `BOBCAT_SPEC_CONSOLE=0` | stay silent — in a terminal, with nothing listening |
| unset | the default above decides |

The variable is deliberately a **tri-state**. Collapsed to a bool, "somebody said no" and "nobody
said" are the same value, and `BOBCAT_SPEC_CONSOLE=0` would have printed anyway.

`BOBCAT_SPEC_PREVIEW=1` is the other half — every projected specification **without** its results,
the projected lane's answer to [`preview`](integrating-gherkin.md#preview),
including which helper each step binds to. Pair it with the platform's own `--list-tests` to preview
without executing anything at all: the plan is registered by a module initializer, so it is known
before a single test runs.

```bash
BOBCAT_SPEC_PREVIEW=1 ./MySpecs --list-tests
```

Both are written at **process exit**, not after the last test — nothing here knows which test is the
last one, and the runner does not say. `ProjectedSpecConsole.Captured` is the seam for a consumer
that would rather render or assert on the specifications itself.

Two things worth knowing about the report's shape. Scenarios are ordered **alphabetically within a
feature**, because a test runner is free to run tests in any order and in parallel, so finish order
changes between runs of an unchanged suite and makes two reports impossible to diff — source order
would be better still, and is a compile-time fact the generator does not currently register. And a
scenario that declares no steps renders as `OK` with a dim "declares no steps" line, where Bobcat
treats a zero-step scenario as a pending-specification hotspot everywhere else.

## Checks that gather instead of throwing

An assertion library throws, and a throw ends the test method — so a projected specification could
only ever show its *first* disagreement, with every later step unrun. Storyteller's
`Asserting_Values.md` sample is the case that makes it concrete: it reaches five sentences and shows
five verdicts, and its own comment says the middle one was written wrong on purpose.

`SpecAssert` is how a step reports a comparison without ending the test:

```csharp
[Then("The value should be {value}")]
internal void TheValueShouldBe(double value) => SpecAssert.Check("value", _calculator.Value, value);
```

| | |
|---|---|
| `SpecAssert.Check(name, actual, expected)` | one expected/actual cell, named for the placeholder it belongs to — so the verdict renders *in* the sentence where the value sits. Takes `rowIndex:` for a table row |
| `SpecAssert.Fact(condition, because)` | a boolean verdict with no pair of values |
| `SpecAssert.Fail(message)` | a failure the step describes itself |
| `SpecAssert.Gather(action)` | run an assertion that *does* throw — Shouldly, xUnit, NUnit — and record it as this step's failure instead of ending the test. **Only an assertion** is gathered; anything else is rethrown unchanged, because swallowing a `NullReferenceException` would turn a broken test into a merely red one |

`Check` runs the same `CellCheck` comparison the Gherkin lane's return-value verification runs, so a
projected check and a `.feature` check on the same value agree and render identically.

**The runner still has to be told**, or a red specification would be reported as a green test. The
gathered wrongs become the test's verdict once, at the end, when the adapter's after-test hook throws
them — xUnit and TUnit both fold an exception from there into the test's own result:

```
failed CalculatorSpecs.asserting_values
  SpecAssertionException : 1 specification step failed:
    And For X=4 and Y=4, the Sum should be 6 and the Product should be 8 => Sum: expected '6', got '8'
```

A `bool`-returning step is the other shape, unchanged from Storyteller: the answer *is* the verdict,
with no `[Check]` and nothing reported by hand. `Task<bool>` works the same way and carries a real
duration.

## Tables and sets from a C# test

A C# test has no trailing `|...|` block, so a table arrives as **pipe-delimited text** that
`StepTable` reads by an implicit conversion:

```csharp
[Given("the roster is")]
internal void TheRosterIs(StepTable roster) { /* … */ }
```

```csharp
_roster.TheRosterIs("""
    | player       | position |
    | Nolan Ryan   | Pitcher  |
    | Johnny Bench | Catcher  |
    """);
```

**One grammar body serves both lanes.** `TheRosterIs(StepTable roster)` is the same method a
`.feature` file binds to; the document supplies the table there and the caller supplies it here, and
nothing in the grammar knows which. Both lanes then render the same grid, because a table becomes
cells carrying a row index plus the column order either way.

A markdown table pastes in unchanged — the alignment row is recognised and dropped, outer pipes are
optional, cells are trimmed — so the table in the specification, the table in the pull request and
the table in the test are the same text. What is deliberately not supported is markdown's escaping
and inline formatting: a cell is the text between pipes, because that is what a Gherkin cell is, and
two rules for reading a cell is how the lanes would drift.

The two alternatives were both worse. Calling a row helper once per row renders as N steps and loses
the grid. A collection-of-tuples argument (`void Sum((int x, int y, int sum)[] rows)`) grids up fine
but is a second signature written for the C# lane beside the one the document binds to, so a change
to the vocabulary has to be made twice.

### A table of objects, and a decision table

`BuildRows<T>` is Storyteller's `CreateNewObject<T>` — the rows *are* the input, built through the
same cell conversion the Gherkin lane uses, so `TODAY+30` is a date and a field the record defaults
need not appear in the table at all:

```csharp
[Given("the signings are")]
internal void TheSigningsAre(StepTable table)
{
    _signings.Clear();                                         // before all rows
    _signings.AddRange(TableRunner.BuildRows<Signing>(table));
    // one save, here                                          // after all rows
}

public record Signing(string Player, Position Position, DateOnly StartsOn, int Years = 1);
```

```csharp
_roster.TheSigningsAre("""
    | Player       | Position | StartsOn |
    | Nolan Ryan   | Pitcher  | TODAY    |
    | Johnny Bench | Catcher  | TODAY+30 |
    """);
```

A **decision table** is the same shape with the grammar reporting one cell per row, so the grid
carries a verdict per row:

```csharp
[Then("adding numbers together")]
internal void AddingNumbersTogether(StepTable sums)
{
    var rows = sums.AsDictionaries();

    for (var i = 0; i < rows.Count; i++)
        SpecAssert.Check("sum", int.Parse(rows[i]["x"]) + int.Parse(rows[i]["y"]),
            int.Parse(rows[i]["sum"]), rowIndex: i);
}
```

The comparison supersedes the value the literal wrote for that column, which is why a wrong row reads
`expected '5', got '4'` in the `sum` column rather than echoing the `5` the test typed.

On a grammar that inherits `Fixture`, `BuildRows<T>(table)` and `RunTable(nameof(rowMethod), table)`
are there directly; `TableRunner` is public so a grammar that is a plain class can reach the same two.
Everything these share with the Gherkin lane — cell tokens, `[Header]`, optional columns, a row that
throws — is in [Data Intensive Specifications](tutorials/data-intensive-specifications.md).

### A set verification a C# test can make

`[SetVerification]` is declarative: the method returns the **actual** collection and the generator
supplies the **expected** rows from the document. That is what makes it readable by a tool — and what
puts it out of reach of a C# test, which has no way to hand it an expectation. It is the same wall a
named-tuple return hits.

A step taking a `StepTable` has no such problem, because the expected rows are an argument:

```csharp
[Then("the unordered details should be")]
internal void TheUnorderedDetailsShouldBe(StepTable expected)
    => SetVerificationComparer.Verify(_details, expected, keyColumns: "Name");
```

```csharp
_sets.TheUnorderedDetailsShouldBe("""
    | Amount | Date    | Name       |
    | 10     | TODAY-2 | Socks      |
    | 200    | TODAY-1 | The Pants  |
    | 100    | TODAY   | The Shirts |
    """);
```

```
    ✗ Then  the unordered details should be
╭───┬─────────────────────────┬──────────────────────┬────────────┬─────────╮
│ # │ Amount                  │ Date                 │ Name       │ Status  │
├───┼─────────────────────────┼──────────────────────┼────────────┼─────────┤
│ 1 │ expected '11', got '10' │ 2026-09-29 (TODAY-2) │ Socks      │  FAIL   │
│ 2 │ 200                     │ expected …, got …    │ The Pants  │  FAIL   │
│ 3 │ 100                     │ TODAY                │ Sweatpants │ MISSING │
│ 4 │ 100                     │ 2026-10-01           │ The Shirts │  EXTRA  │
╰───┴─────────────────────────┴──────────────────────┴────────────┴─────────╯
```

The comparison is identical to the declarative form — the same comparer, the same key matching, the
same order-after-matching rule, the same four row markers, the same grid. Only where the expected
rows come from differs. On a `Fixture` the call is `VerifySet(actual, expected, keyColumns: "Name")`.

**Prefer `[SetVerification]` where it reaches.** Its `KeyColumns`, `Ordered` and `Column` are
compile-time facts, which is what lets the preview and the editor read them and what makes BOBCAT014
and BOBCAT031 compile errors; as arguments, nothing can see them before the step runs. One thing the
argument form does better: a set of plain values needs no `Column` at all, because the table is in
view and has exactly one.

### One object against one row

The same shape for a single object — Storyteller's `VerifyObject`, with the columns the row names
compared against the properties of those names:

```csharp
[Then("the address should be")]
internal void TheAddressShouldBe(StepTable expected) => PropertyCells.Verify(_address, expected);
```

```csharp
_objects.TheAddressShouldBe("""
    | Address1     | Address2 | City   |
    | 3 1st Street | EMPTY    | Dallas |
    """);
```

**Only the columns the row names are compared.** An address has six fields and a specification
naming three means nothing by the other three — #241's partial rule again, not a second convention.
On a `Fixture` the call is `VerifyObject(subject, expected)`, the sibling of `VerifySet`, `RunTable`
and `BuildRows`.

This is the one grammar family with **no declarative twin**, because there is nothing to configure:
the columns come from the table and the subject from the method. See
[Data Intensive Specifications](tutorials/data-intensive-specifications.md#one-object-against-one-row).

## Projecting the assertions you already wrote (opt-in)

With `<BobcatProjectAssertions>true</BobcatProjectAssertions>` in the project, an ordinary
statement-level Shouldly call inside a `[BobcatFeature]` test renders as a step — no attribute, no
helper, nothing moved:

```csharp
[Fact]
public void every_assertion_in_a_run_is_evaluated()
{
    var calculator = new Calculator { Value = 3 };

    // Then the calculator agrees about its value
    calculator.Value.ShouldBe(3);
    calculator.Value.ShouldBeGreaterThan(10);
    calculator.Value.ShouldBeLessThan(2);
    calculator.Value.ShouldBe(3);
}
```

```
  every assertion in a run is evaluated FAILED
    ✗ Then  the calculator agrees about its value
      ✓ Then  calculator.Value should be 3
      ✗ And   calculator.Value should be greater than 10
          ✗ calculator.Value: expected '10', got '3'
      ✗ And   calculator.Value should be less than 2
          ✗ calculator.Value: expected '2', got '3'
      ✓ And   calculator.Value should be 3
```

A **run** of consecutive assertions is all evaluated before the next action, and the run's failures
are thrown at its end — the point just before the next action, which would be operating on state the
assertions have already shown to be wrong. Everything after that renders as never reached. A plain
Shouldly test reports the first failure and leaves three blanks; here all four reach the report and
the test still fails, once.

**A cell says which comparison it made**, so a non-equality assertion cannot claim an equality it
never checked:

```
    ✗ Then  the value fails four different comparisons
      ✗ Then  calculator.Value should be greater than 10
          ✗ calculator.Value: should be greater than '10', got '3'
      ✗ And   calculator.Value should be less than 2
          ✗ calculator.Value: should be less than '2', got '3'
      ✗ And   calculator.Value should not be 3
          ✗ calculator.Value: should not be '3', got '3'
      ✗ And   calculator.Value should be 3.5, 0.01
          ✗ calculator.Value: should be approximately '3.5', got '3'
```

Each of those used to read `expected 'N', got '3'`, which is false for every one of them — `N` is a
bound, not an expectation. The *sentence* was always right, because the dialect writes the comparison
into the step text; only the cell lied, which is why it went unnoticed.

**An assertion Bobcat cannot describe produces no cell at all.** The comparison comes from a closed
set, and `ShouldBeTrue`, `ShouldBeEquivalentTo`, `ShouldBeOfType` and `ShouldBeInRange` are not in
it — their expectation is not a value to put beside an actual, so any row shape would state something
false. The step still renders, with its verdict and duration; only the cell is withheld. The choice
is made at compile time, so the runtime never has to judge whether a cell it was handed is
describable.

**Only a statement-level call is projected.** `x.ShouldNotBeNull().Name.ShouldBe("a")` consumes the
first assertion's result, so gathering it would dereference null and report a
`NullReferenceException` instead of the assertion that failed. A call whose value is used is left
alone, which also means a fluent chain is safe by construction.

**Shouldly is the dialect Bobcat supports for 1.0.** `IAssertionDialect` in the generator is the
seam, and FluentAssertions is the intended second. `Assert.*` is *not* planned: its subject is an
argument whose position differs per assertion, so a sentence built from one rule reads backwards, and
a per-method table of every xUnit assertion is a maintenance burden with no ceiling.

## Listing and running one specification

A projected suite answers the same two lane-neutral questions a Gherkin one does — *what do you
specify*, and *run exactly this* — because a monitor only ever names identities and never a test
framework's own filter:

```bash
BOBCAT_LIST_SPECS=/tmp/specs.json ./MySpecs --list-tests
```

The manifest it writes carries the lane (`projected`), the framework (`xunit`), and every
`{Feature}/{Scenario}` with the class and method it binds to — which is the fact a framework's filter
needs and the identity cannot supply. Bobcat then translates an identity into
`--filter-method Ns.Class.method`, where the Gherkin lane's identity *is* the platform uid and goes
through unchanged. See [Spec Identities](spec-identities.md#listing-and-running-by-identity)
for the whole rule, and [The Resident Runner](resident-runner.md) for the loop a console drives it
from.

Two details matter if you are reading the manifest yourself. The binding is **recorded, not
re-derived** — the identity is a one-way function of the names, and an explicit
`[BobcatFeature("…")]` title shares nothing with its class name. And it is emitted on a wider rule
than declared steps: what makes a test a specification is that `[BobcatScenario]` records it, not that
it declared any steps. Keyed on declared steps, the first cut of this listed **8 of
`Bobcat.Xunit.Samples`' 41 specifications** while the other 33 rendered and published verdicts
perfectly well.

## Declared is not executed

Two different things, kept apart on purpose:

- **Declared steps** are what the test *says* it does, read from marker comments at compile time
  and known before a line of it runs. That is what lets a scenario announce "step 2 of 4" up
  front, and it is trustworthy precisely because it was never inferred from what happened.
- **Recorded steps** are what actually ran, with a duration and a verdict. They come from
  step-attribute interceptors.

`DeclaredSteps.For("{Feature}/{Scenario}")` reads the first at runtime; every `DeclaredStep`
carries the source line its comment came from.

## What reaches the viewer

A scenario publishes `ScenarioStarted` (with the declared step count and, since #304, the
declared sentences themselves), `StepStarted` as each step **opens** — carrying
`DeclaredStepNumber`, the sentence it ran under — `StepFinished` with that step's own verdict, and
`ScenarioFinished`. Steps are announced when they open rather than when they end, because a
watcher looking at a run in flight needs the step that is currently taking the time — which is
exactly the one that has not finished yet.

The narrative rides on `ScenarioStarted` rather than arriving as steps, and that is the wire
shape of "declared is not executed": publishing a declared step as a `StepStarted` would be
announcing that it ran.

With no viewer listening the publisher is null and the whole thing costs a few strings per test.

## A step need not be a keyword — `// *` (issue #324)

Bobcat's rendering does not depend on Given / When / Then, and should not. The inspiration is
ThoughtWorks' **Gauge**, whose specifications are bulleted sentences with no keyword vocabulary at
all, and **Storyteller**, whose specs read as prose rather than as a keyword table. Plenty of steps
are simply not one of five words:

```csharp
[Fact, BobcatScenario]
public async Task the_overnight_sweep_reconciles_every_wallet()
{
    // * two wallets, one of them credited twice
    await Seed();

    // * the overnight reconciliation runs
    await Sweep();

    // Then every balance agrees with its events
    await AssertBalances();
}
```

Renders as three steps, the first two carrying no keyword at all. Keywords still work and still
mean what they did — this is an addition, not a replacement, and the two mix freely in one test.

**Why the bullet is required.** Most comments in a test body are not steps, so treating every comment
as one would bury the real steps in noise. `*` is one character of opt-in, unmistakably deliberate,
and the same character Gauge uses. `// just explaining the next line` stays a comment.

The runtime needed no change for this: `DeclaredStep.ToString()` already omitted an empty keyword
rather than emitting a leading space. What was missing was any way to *write* one — and four
rendering sites that would have shown an empty `<strong>`.

## Binding a projected test to a slice — `[BobcatSlice]` (issue #324) {#bobcatslice}

`[BobcatFeature]` makes a projected test *readable*. It said nothing about **which slice the test is
evidence for**, so the slice stayed unbound on the Event Model however green the test was, and no
spec-identity gate could join it. `[BobcatSlice]` closes that:

```csharp
[BobcatFeature("Booking appointments")]
[BobcatSlice(SliceType = typeof(ConfirmAppointment), Domain = "Scheduling", Chapter = "BookingAppointments")]
public class BookingSpecs
{
    [Fact, BobcatScenario]
    public async Task a_proposed_appointment_is_confirmed() { /* … */ }

    // One class, several slices: a method-level binding REPLACES the class's.
    [Fact, BobcatScenario]
    [BobcatSlice(SliceName = "ProposeHomeCheckAppointment", Pattern = "Automation")]
    public async Task an_accepted_assignment_proposes_a_visit() { /* … */ }
}
```

### Binding only — never roles

This says *which* slice. It never says what the slice's command, events or read models are. The
curated model states those on the **Declared** rung and the code states them on **Derived**; a third
opinion from a test can only manufacture a `SourceDisagreement` nobody can act on — see issue #323
for what that costs when it happens. A projected test's contribution is **evidence**: a bound
`{Feature}/{Scenario}` identity, which is what turns the slice from unbound to observed.

So a projected test's slice shows the roles the model and the code agree on, and the test's identity
alongside them. It merges by name with every other authoring style — a slice can carry Gherkin, HTTP,
Gherkin and projected specifications at once, because a slice is a vertical behaviour and not an
authoring style.

### Two spellings, and why `SliceType` is preferred

`SliceType` means **exactly** `SliceName = type.Name`. No suffix stripping, nothing inferred — which
is what makes it safe, because passing `typeof(ConfirmAppointmentHandler)` plainly would not produce
the slice name rather than being quietly "corrected".

Prefer it for two reasons. It is rename-safe, and — the one that matters more — **a type survives to
the generator where a string does not**, so only `SliceType` can later be cross-checked against what
the model declares.

Reach for `SliceName` where no type bears the slice's name. That is not an edge case: measured across
CritterCrush's nineteen slices, Command slices matched a type 13/13 and View slices 3/3, while
**Automation slices matched 0/3** — an automation's only type is `{Slice}Handler`, and it has no
command type at all, by design.

### Three diagnostics, so the preference is enforced rather than remembered

| | |
|---|---|
| **BOBCAT023** (error) | Both spellings set, naming *different* slices. One of them is wrong and no silent winner is the right answer. Setting both to the *same* slice is redundant, not wrong, and is allowed |
| **BOBCAT024** (warning) | `SliceName` is a literal string and a type of that name exists in this compilation — use `SliceType`. Silent where no such type exists, which is every Automation |
| *(not a generator diagnostic)* | "`SliceType.Name` matches no slice in the model" cannot live here: the generator has no curated model to check against. It belongs to the model merge, where a binding naming nothing shows up as an unmatched slice |

### A method-level binding replaces, it does not override

A rebinding is a *whole* binding. An earlier cut merged the method's tags over the class's, and a
method rebinding to another slice dragged the class's `Domain` / `Chapter` / `Pattern` with it —
stamping them on a slice that already had its own answers. So a test that wants the class's domain as
well restates it.

### One attribute instead of three — `[BobcatSpec]` (issue #403) {#bobcatspec}

A projected xUnit spec used to carry three attributes: the `[Fact]`, the `[BobcatSlice]` that binds
it, and the `[BobcatScenario]` that opens the recording. `[BobcatSpec]` is all three:

```csharp
// Before
[Fact, BobcatSlice(SliceType = typeof(ConfirmAppointment))]   // plus [BobcatScenario] on the class

// After
[BobcatSpec(typeof(ConfirmAppointment))]
public async Task a_proposed_appointment_is_confirmed() { /* … */ }
```

`typeof(X)` positionally means exactly `SliceName = "X"`, the same rule `SliceType` follows.
`SliceName`, `SliceType`, `Domain`, `Chapter` and `Pattern` are all settable by name as well, with
BOBCAT023 and BOBCAT024 unchanged — including **across the two attributes**, because they are the
same settings under two spellings and a method carrying both is one binding stated twice.

**It closes the BOBCAT028 trap by construction.** That diagnostic exists because a class can bind a
slice and open no recording — green tests, an unbound slice, and nothing at run time able to tell you
why. The attribute that *claims* the slice is now the attribute that *opens* the recording, so the
two cannot come apart.

**xUnit-only, for now.** It subclasses `Xunit.FactAttribute`, so TUnit needs its own equivalent;
that is a follow-up. `[BobcatScenario]` stays for a class-level opt-in covering every test in a
class, and for suites already written against it.

### Stub-first: `Pending = true` (issue #404)

Stub-first work produces specifications that exist before their behaviour does. In the Gherkin lane
a scenario with **no steps** is already a `PendingSpecification` hotspot (jasperfx#689) and
`SpecIdentityAudit` treats it as *joined*, not drift. The projected lane had no equivalent: a
scaffolded skeleton **threw**, so a pending spec was indistinguishable from a failing one — in the
test report and on the canvas both.

```csharp
[BobcatSpec(typeof(ConfirmAppointment), Pending = true)]
public async Task a_confirmed_appointment_cannot_be_confirmed_twice()
    => throw new NotImplementedException();   // never runs
```

Three things follow, and they are the issue's acceptance:

| | |
|---|---|
| **A hotspot, not a specification** | The identity lands on the slice as `HotspotDescriptor.PendingSpecification`, so the slice does not look *verified* by a stub — while still being **joined**, so the audit reports neither an orphan nor a hole |
| **Skipped, never swallowed** | It sets `FactAttribute.Skip`, so every runner and IDE reports it as a skip: nobody ran it. Running the body and absorbing the failure would launder red into green, which is the one thing a pending marker must not do. An explicit `Skip` of your own wins, in either written order |
| **It disappears on its own** | Delete `Pending = true` and it is an ordinary specification again. There is no second place to update, which is what keeps a stub from outliving its stub phase |

**"No steps" does NOT mean pending here**, deliberately, and that is the one place the two lanes
differ. A Gherkin scenario with no steps says nothing. A projected test with no marker comments says
plenty — it runs real code and publishes a real verdict — and **33 of this repository's own 41
projected samples declare no marker steps at all**. Inferring pending from an empty step list would
have turned most of a working suite into open questions on the canvas.

**It is on `[BobcatSpec]` and not on `[BobcatSlice]`.** Putting it there would let a TUnit suite
write it, but that attribute is not the test: it would produce the hotspot while the body still ran
and still failed — precisely the "indistinguishable from a failing one" state this exists to end. A
TUnit suite gets it with TUnit's own equivalent of `[BobcatSpec]`.

A pending spec **stays in the spec manifest and in `--list-tests`**, because xUnit discovers a
skipped test like any other. That is the right answer rather than an accident: a resident runner can
be asked to run it, and gets a skip.

Two implementation facts worth knowing, both pinned by tests rather than left as comments:

- **It is both a Fact and a test-bracket hook because xUnit v3 collects hooks by the
  `IBeforeAfterTestAttribute` *interface***, not only from the `BeforeAfterTestAttribute` base
  class. Those two are siblings — both derive straight from `Attribute` — so inheriting from both
  was never possible.
- **It re-declares and forwards `[CallerFilePath]`/`[CallerLineNumber]`, and a subclass of it must
  too.** `FactAttribute`'s only constructor takes that pair and the compiler fills them at the call
  site it sees; a subclass that calls `base()` without re-declaring them hands over *its own* file
  and line, so every test in the suite reports one source location and IDE test navigation lands on
  the attribute instead of the test.

## Saying a slice is specified here — retired (issue #406)

The `*.spec-ownership.yaml` manifest, and its two build-time checks `BOBCAT025` and `BOBCAT026`, were
retired on 2026-10-06 along with the rest of Bobcat's YAML authoring surfaces. The manifest existed to
declare, before any code existed, that a slice would be specified as a projected test rather than a
`.feature` — which mattered chiefly so that Bobcat's scaffolder wrote the right kind of skeleton, and
that scaffolder is now Wolverine's `scaffold` command.

`[BobcatSlice]` is unaffected: binding a test that already exists to a slice is the half that was
always the more useful one, and it is still here.

**What is genuinely gone, with no replacement yet:** declaring a slice's authoring lane *forward*, and
the two checks that came with it — a slice specified in two lanes (an error, and the duplicate-identity
guard), and a manifest naming a slice nothing binds (a warning). The intent is for that content to move
onto attributes; until it does, nothing declares a slice's lane before the spec exists. The gate that
still works is [Checking Spec Identities Against the Model](spec-identities.md), which compares spec
identities rather than lanes.


## The honest limits

- **A comment-declared step still has no clock of its own.** It reports the work observed *inside*
  it (issue #304) — the decorated helpers that ran under it, their verdicts, and the sum of their
  durations — and a region containing none of them says nothing at all. Timing needs somewhere to
  intercept and a comment does not give one; what changed is that the work underneath it is now
  attributed to it, exactly, from the call site's line.
- **A step outside a scenario is silent.** Decorated helpers get called from plenty of places that
  are not specifications, and reporting from them would be noise.
- **An exception ends the test, so the steps after it are never judged.** This is the one
  irreducible difference between a spec engine and a test method: Storyteller's
  `Facts_in_Action.md` reaches all five of its lines and the projected version reaches four. It is a
  design question rather than a bug — a continue-past-an-exception step bracket would have to
  swallow and record, which changes what a test body means. `SpecAssert` is the way around it for a
  comparison you *expect* might disagree.
- **A marker comment cannot carry a cell.** A comment *declares* a step; it does not execute one, so
  there is no step object for a comparison to attach to. A narrated test's finest available verdict
  is its own exception — one failure, with no expected/actual pair. A decorated helper reporting
  through `SpecAssert.Check` is what gets the cell.
- **A projected step has no logs and no diagnostics.** The Gherkin lane renders both; a recorded step
  carries neither, so Storyteller's `Context.Reporting.Log(...)` has no equivalent yet.
- **Test methods are matched by attribute name** — `Fact`, `Theory`, `Test`, `TestCase` — so the
  generator needs no reference to a runner it is trying to stay neutral about.

## The diagnostics this lane adds

| | |
|---|---|
| **BOBCAT027** (warning) | A step template names no parameter of its method. Nothing can fill it, so the step renders as `{thread}` forever — before this it looked identical to a placeholder that was merely deferred |
| **BOBCAT028** (warning) | A `[BobcatFeature]` class that never opens a recording, because neither `[BobcatScenario]` nor `[BobcatSpec]` is on it. Every step records into nothing and the suite passes having produced no specification at all. **Nothing about a run can report this** — a suite that records nothing is indistinguishable at run time from a suite with nothing to record — which is why it is a compiler diagnostic |
| **BOBCAT029** (info) | A comment opening with `And` or `But` where no narrative is open: ordinary prose, not a step. English sentences begin "And …" constantly, so a keyword that can only *continue* a narrative must not be able to start one. Info rather than a warning, because the common case is that the comment really is prose — it is here for the one author who meant a step and cannot see why it is missing |
| **BOBCAT030** (error) | A written value that cannot be read as the type of the parameter it binds to. It suppresses the feature, because the alternative is what happened before it existed: a `CS0103` or `CS1503` inside a generated file the author cannot open |
| **BOBCAT031** (error) | A set verification over a collection of plain values with no `Column` named for them |

The honest limit on BOBCAT029, pinned by
`Bobcat.Acceptance.Tests/MarkerCommentTighteningTests`: inside a test that is *already* narrating,
`And …` is taken at its word. There is no way to tell that sentence from a step, and guessing from
its wording would be worse than a rule an author can learn.

## The sample corpus

`src/Bobcat.Xunit.Samples` is Storyteller 5's own sample suites recreated one specification at a
time as ordinary xUnit v3 tests — sentences, facts, output parameters, tables, sets, decision
tables, narrated tests, and the projected-assertion style. **Twenty-four of its forty-one
specifications fail on purpose**, because the samples exist to show what each outcome *looks* like.
`src/Bobcat.Gherkin.Samples` is the same documents in the Gherkin lane, which is what keeps the two
renderings honest about each other.

```bash
dotnet build src/Bobcat.Xunit.Samples/Bobcat.Xunit.Samples.csproj
./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples
```
