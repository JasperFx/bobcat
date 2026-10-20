# Storyteller's sample specifications, as projected xUnit tests

Storyteller 5's own sample suites, recreated one specification at a time as ordinary xUnit v3 tests
that project into the Bobcat model. The point is **rendering**: Storyteller's samples were written to
show what each outcome looks like — a wrong value, a false fact, a thrown exception, a critical stop
— so they are the right corpus for working out what Bobcat's specification output should say.

Sources: [`storyteller/Storyteller`](https://github.com/storyteller/Storyteller) at `master`
(version 5.4.0) — `src/Samples` (the documented samples) and `src/StoryTeller.Samples`.

## Running it

```bash
dotnet build src/Bobcat.Xunit.Samples/Bobcat.Xunit.Samples.csproj

# the test runner's own output, plus the rendered specifications — a terminal with nothing
# listening on the wire prints them by default now (issue #384)
./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples

# ...the runner's output only
BOBCAT_SPEC_CONSOLE=0 ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples

# ...the specifications even into a pipe, or with a console listening
BOBCAT_SPEC_CONSOLE=1 ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples | less -R
```

```bash
# ...or the specifications WITHOUT running them — the projected lane's `bobcat preview`
BOBCAT_SPEC_PREVIEW=1 ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples --list-tests
```

**Thirty-one of the seventy-four specifications fail on purpose**, which is why `IsTestProject` is `false`:
`dotnet test` never collects this project, and a red run here is the samples working.

`Specs/RecoveryHintProbes.cs` is not a Storyteller sample. It's three specifications that
`Bobcat.Supervisor.Tests` drives to prove a supervisor honours the recovery hints a projected suite
declares on itself (issue #469). They're green unless `BOBCAT_HINT_PROBE` is set.

## What is covered

| Storyteller sample | Recreated as | Outcome |
|---|---|---|
| `Specs/Calculator/Using_Sentences.md` | `CalculatorSpecs.using_sentences` | green |
| `Specs/Calculator/Bad_Values.md` | `CalculatorSpecs.bad_values` | one wrong cell |
| `Specs/Calculator/Using_Output_Parameters_in_a_Sentence.md` | `CalculatorSpecs.using_output_parameters_in_a_sentence` | green, two cells |
| `Specs/Assertions/Asserting_Values.md` | `CalculatorSpecs.asserting_values` | wrong in the middle, later steps still judged |
| `Fixtures/CriticalThrowingFixture.cs` | `CalculatorSpecs.an_exception_in_an_action_stops_the_scenario` | error, scenario stops |
| `Specs/Facts/Facts_in_Action.md` | `FactSpecs.facts_in_action` | wrong + error (see gap 1) |
| the same, minus the throwing line | `FactSpecs.facts_in_action_without_the_throwing_line` | three wrongs, all reached |
| `Specs/Actions/Explicit_Action.md` | `ActionSpecs.explicit_action` | green, real duration |
| `Specs/Actions/Implicit_Action.md` | `ActionSpecs.implicit_action` | green |
| `Fixtures/LoggingFixture.cs` | `ActionSpecs.custom_logging_from_a_step` | green (see gap 3) |
| `Specs/Currying/Currying.md` | `CurryingSpecs.currying` | green, nested grammar |
| `Fixtures/AsyncOperationsFixture.cs` | `AsyncSpecs.*` | green / wrong / error |
| `Specs/Tables/Table_with_Options.md` | `TableSpecs.a_table_of_setup_data` | green, one column left out |
| `StoryTeller.Samples/Specs/Tables/Tables.md` | `TableSpecs.a_decision_table_with_a_wrong_answer` | one wrong row |
| — (a Storyteller decision table with output parameters) | `TableSpecs.a_decision_table_with_out_parameters` | green, two out-parameter columns and a returned value |
| `Specs/Sets/Object_Sets.md` | `SetSpecs.an_unordered_set_*` / `every_cell_*` / `an_ordered_set_*` | green / two wrong cells + a mismatch / a reordering |
| `Specs/Sets/Data_Tables.md` | `SetSpecs.every_row_*` / `the_database_has_*` / `the_specification_expects_*` / `a_row_whose_key_*` | green / extra row / missing row / mismatch |
| `Specs/Sets/String_Lists.md` | `SetSpecs.a_set_of_names_*` | a reordering, and a name nobody has |
| `Specs/Create Objects/Using_VerifyObject.md` | `ObjectSpecs.only_the_properties_the_document_names_are_compared` | green, three of six fields |
| `StoryTeller.Samples/Specs/General/Check properties.md` | `ObjectSpecs.every_named_property_agrees` / `one_property_disagrees` / `every_property_disagrees` | green / one wrong column / every column wrong |
| — | `NarratedSpecs.*` | the marker-comment style, for contrast |
| — (`docs/getting-started.md`) | `GettingStartedSpecs.using_sentences` | the first specification over the cut-down `CalculatorSteps` grammar; green |
| — (`docs/tutorials/specifications-with-code.md`) | `WithdrawalSpecs.*` | the tutorial's specs over `Account`: narrated, vocabulary, decision table, set verification, theory; three fail on purpose for the page's failure renderings |

Not yet: the five `Create Objects` documents about *constructing* an object rather than checking one
(`Set_Up_a_Single_Address`, `As_Table`, `Using_ObjectIs`, `Using_WithInput`, `Using_LoadObjectBy` —
`BuildRows<T>` covers the table form), `ApiFixture`, `ModelFixture`, selection lists, `Arrays.md`
(no collection capture), Paragraphs (deliberately out of scope).

## Tables: a table literal in the test

A C# test has no trailing `|...|` block, so a table arrives as **pipe-delimited text** that
`StepTable` reads by an implicit conversion — see `Grammars/RosterGrammar.cs` and `Specs/TableSpecs.cs`:

```csharp
_roster.TheRosterIs("""
    | player       | position |
    | Nolan Ryan   | Pitcher  |
    | Johnny Bench | Catcher  |
    """);
```

**One step, one grid.** `TheRosterIs(StepTable roster)` takes the whole table; the recorder turns it
into cells carrying a `RowIndex` plus the column order, and `SetVerificationRender.FromCells` folds
those back into a grid.

The two alternatives were worse. Calling a row helper once per row renders as N steps and loses the
grid. A collection-of-tuples argument (`void Sum((int x, int y, int sum)[] rows)`) grids up fine but
fixes the columns in the signature, so an optional column or a reordering needs a new overload.

A markdown table pastes in unchanged — the alignment row is recognised and dropped, outer pipes are
optional, cells are trimmed — so the table in the specification, the table in the pull request and
the table in the test are the same text. What is deliberately not supported is markdown's escaping
and inline formatting: a cell is the text between pipes, and one rule for reading a cell is what keeps
a table literal and an expected value agreeing about what a word means.

A **table of objects** is `TableRunner.BuildRows<T>(table)` — Storyteller's `CreateNewObject<T>`,
with Bobcat's cell conversion (`CellValues`), so `TODAY+30` is a date and a field the record
defaults need not appear in the table at all. Its "before all rows" and "after all rows" hooks are the
lines either side of the call; Storyteller needed hooks because the table was declared rather than
called. `TableRunner.Run(this, nameof(...), table)` calls a row method once per row.

A **decision table** works the same way, with the grammar reporting one cell per row —
`SpecAssert.Check(name, actual, expected, rowIndex: i)` — so the grid carries a verdict per row. The
comparison supersedes the value the literal wrote for that column, which is why a wrong row reads
`expected '5', got '4'` in the `sum` column rather than echoing the `5` the test typed.

## Sets: a set verification the grammar makes itself

A set verification is a step taking a `StepTable`: the expected rows are an argument, and the call
site supplies them. See `Grammars/SetsGrammar.cs` and `Specs/SetSpecs.cs`:

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

The comparison is `SetVerificationComparer.Verify`, with key columns, ordering and column selection
as arguments. A set of plain values needs no `Column` at all: the table has one column, so there is
only one thing it could be. A constant table's columns are checked against the element type at build
time wherever that type is known (BOBCAT032).

The four outcomes in `Data_Tables.md` are the reason it is worth recreating: a happy path, an extra
row, a missing row and a mismatch, all through one grammar, which is how the row verdicts get read
side by side.

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

A **mismatch reads as one missing row beside one extra**, not as a row with a wrong cell, whenever
the disagreeing column is a key column — row 3 and row 4 above are the single `Sweatpants`/`The
Shirts` disagreement. That is `KeyColumns` doing its job: naming fewer of them is what turns a
mismatch back into a cell-level difference, and `Data_Tables.md`'s "Mismatch in Rows" is the sample
that shows it.

## Objects: one object against one row

Storyteller's `VerifyObject` from the C# side — `Grammars/ObjectsGrammar.cs` and `Specs/ObjectSpecs.cs`:

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

The comparison is `PropertyCells` underneath and `CellCheck` under that, so a property column and a
set column disagree in the same words.

Only the columns the row names are compared. There is no attribute form, deliberately: there is
nothing to configure, so an attribute would carry no information.

## One attribute family, two expression syntaxes

`[Given]`, `[When]`, `[Then]`, `[Check]` and the keywordless `[Step]` all derive from one
`StepAttribute`, and every one of them is intercepted at the call site when a test calls the method.
`[BobcatStep]` is the legacy spelling of the same thing.

Each expression is a **Storyteller `[FormatAs]` template**: every placeholder names a parameter
(`{sum}`) and is filled from that argument. Cucumber type captures (`{int}`, `{string}`, `{word}`)
are not supported — they went with Gherkin, and a placeholder naming no parameter is BOBCAT027.

Facts are Storyteller's shape, unchanged: a `bool`-returning step whose answer is the verdict, with no
`[Check]` and nothing reported by hand. `Task<bool>` works the same way and carries a real duration.

A **named tuple** return whose elements are expected cells does not work at a C# call site: there is
nowhere for the expected values to come from, and BOBCAT027 says so. Take the expected values as
arguments and report each with `SpecAssert.Check` instead (`CalculatorGrammar.SumAndProduct`).

## How a Storyteller grammar becomes a Bobcat one

```csharp
// Storyteller
[FormatAs("Start with {value}")]
public void StartWith(double value) => _calculator.Value = value;

// Bobcat, projected
[BobcatStep("Start with {value}", Keyword = "Given")]
internal void StartWith(double value) => _calculator.Value = value;
```

Three differences, all deliberate:

- **A keyword.** Bobcat's model is Given/When/Then-shaped, so a step says whether it arranges, acts or
  asserts. Storyteller inferred that from position and grammar type. A repeated keyword renders as
  `And`, decided by the recorder — the helper cannot know whether a call is the first of its block or
  the third.
- **`internal`, not `public` or `private`.** The generated interceptor is an extension method (a
  constraint of C#'s interceptor feature), and an extension method cannot reach a private member.
  One keyword per helper is the whole adoption cost.
- **An assertion grammar reports rather than returns.** Storyteller returned the actual value and let
  the engine compare it against the expected cell in the specification file. A projected test has no
  specification file, so the expectation arrives as an argument and the helper reports the comparison:

```csharp
// Storyteller
[FormatAs("The value should be {value}")]
public double TheValueShouldBe() => _calculator.Value;

// Bobcat, projected
[BobcatStep("The value should be {value}", Keyword = "Then")]
internal void TheValueShouldBe(double value) => SpecAssert.Check("value", _calculator.Value, value);
```

`SpecAssert.Check` runs the `CellCheck` comparison, the same one a table cell or a set verification
uses, so the same value is judged the same way wherever it is checked.

## Why the checks do not throw

`SpecAssert.Check` and `SpecAssert.Fact` **record and return**. An assertion library throws, and a
throw ends the test method — so a projected specification could only ever show its first
disagreement, with every later step unrun. `Asserting_Values.md` is the sample that makes this
concrete: Storyteller reached five sentences and showed five verdicts, and its own comment says the
middle one was written wrong on purpose.

The runner still has to be told, or a red specification would be reported as a green test. The
gathered wrongs become the test's verdict once, at the end, when `BobcatScenarioAttribute.After`
throws them — xUnit folds an exception from an after-test hook into the test's own result:

```
failed CalculatorSpecs.asserting_values
  SpecAssertionException : 1 specification step failed:
    And For X=4 and Y=4, the Sum should be 6 and the Product should be 8 => Sum: expected '6', got '8'
```

## Keywords are optional

`SentenceGrammar` spells none, which is how Storyteller and Gauge sentences read:

```csharp
[BobcatStep("Multiply by {multiplier} then add {delta}")]
internal void MultiplyThenAdd(int multiplier, int delta) { … }
```

```
  arithmetic holds OK
    ✓ Start with the number 5
    ✓ Multiply by 3 then add 4
    ✓ The number should now be 17
        ✓ number: 17
```

The keyword is per step, not per suite — `CalculatorGrammar` next door declares one on every step.
An empty keyword is a third state distinct from "unknown": it renders no label, and it is never
promoted to `And`, because `And` is a word the author never wrote.

Storyteller's prose paragraphs between steps map onto marker comments (`SentenceSpecs.sentences`).
A keywordless one opens with `*`, since an ordinary comment has to stay an ordinary comment.

## Projected assertions (opt-in)

With `<BobcatProjectAssertions>true</BobcatProjectAssertions>`, an ordinary statement-level Shouldly
call inside a `[BobcatFeature]` test renders as a step, and a **run** of consecutive assertions is all
evaluated before the next action:

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

The run's failures are thrown at its end — the point just before the next action, which would be
operating on state the assertions have already shown to be wrong. Everything after renders as never
reached.

**Only a statement-level call is projected.** `x.ShouldNotBeNull().Name.ShouldBe("a")` consumes the
first assertion's result, so gathering it would dereference null and report a
`NullReferenceException` instead of the assertion that failed. A call whose value is used is left alone,
which also means a fluent chain is safe by construction.

**Shouldly is the dialect Bobcat supports for 1.0.** `IAssertionDialect` in the generator is the seam;
FluentAssertions is the intended second, and its shape is written down there (`x.Should().Be(5)` needs
the receiver's `.Should()` unwrapped to reach the subject, and the word "should" put back in front of
the verb). **`Assert.*` is not planned:** its subject is an argument whose position differs per
assertion, so a sentence built from a rule reads backwards, and a per-method table of every xUnit
assertion is a maintenance burden with no ceiling.

## What this pass found

Five defects, all fixed here, and three gaps still open.

**Fixed — a synchronous step that threw rendered green.** The emitted interceptor wrapped a
synchronous call in `using (step)`; disposal alone means "the step ended", not "the step failed", so
the exception never reached the step. The asynchronous path never had it, because
`MarkerStepRuntime.Track` catches and reports. Pinned by
`Bobcat.Projected.Acceptance.Tests/SynchronousStepFailureTests`. It hid because the scenario was still red —
the only symptom was that the failing line was the one marked `✓`, and nothing rendered a projected
specification locally for anyone to read.

**Fixed — `Succeeded with Rights: 0, Wrongs: 0` printed in green under a `FAILED` heading.** A
specification can fail with every count at zero, and `Counts.Succeeded` then says *Succeeded*. The
verdict word now comes from the caller that knows it.

**Fixed — a missing row rendered red and left the test green.** `RecordedStep.Status` counted a
`failed` or `invalid` cell and not a `missing` one, and `missing` is exactly what a set
verification's missing-row marker carries. So a specification whose only disagreement was a row the
system never produced reported `MISSING` in the grid and passed as a test. The (since removed)
Gherkin lane had always counted it, and running one grammar from both lanes is what surfaced the
divergence.

**Fixed — an absent row rendered as a row of blanks, in this lane only.** The renderer took the
first cell per column, and in the projected lane a step is handed the table as written *before* it
compares anything — so each column already had an input cell, and the comparer's cell carrying the
value to show in place was never reached.

**Gap 1 — an exception ends the test, so the steps after it are never judged.** `Facts_in_Action.md`
reached all five of its lines; the projected version reaches four. This is the one irreducible
difference between a spec engine and a test method, and it is a design question rather than a bug:
a continue-past-an-exception step bracket would have to swallow and record, which changes what a test
body means.

**Gap 2 — a marker comment cannot carry a cell.** A comment *declares* a step; it does not execute
one, so there is no step object for a comparison to attach to. A narrated test's finest available
verdict is its own exception — one failure, no expected/actual pair. Compare
`NarratedSpecs.a_narrated_value_check_that_disagrees` with `CalculatorSpecs.bad_values`.

**Fixed — a keywordless step printed `Then`, then `And`.** Two causes: the recorder treated the
empty string as a keyword worth promoting, and the renderer collapsed "no keyword" into "unknown
keyword" and fell back to the step's kind.

**Gap 3 — a projected step has no logs and no diagnostics.** `StepRender` has a place for both;
`ScenarioRecorder.RecordedStep` carries neither, so Storyteller's
`Context.Reporting.Log(...)` has no equivalent. See `BatchProcessGrammar.DoSomethingWorthLogging`.

## What a projected specification now renders

- **Input values in italics**, Storyteller's own convention. The spans come from the substitution
  itself rather than from searching the finished sentence for the values, so a value that also
  occurs in the prose cannot mark the wrong run of characters.
- **Steps the run never reached, greyed out** — `○ The number should now be {number} — not run`.
  Only possible because the plan is a compile-time fact (`PlannedSteps`), matched by call-site
  ordinal rather than by text, so it survives a helper called in a loop or behind an `if`. The
  template is shown unresolved: the arguments were never evaluated.
- **Exceptions at the bottom**, formatted by Spectre, with `NotImplementedException — see below` on
  the step itself. A stack trace is the longest thing in a report and the least useful part of
  scanning it; the reader wants to know *which* step broke first. A gathered wrong keeps its message
  on its own line — nothing was thrown, so there is no stack to show. One known cost: the generated
  interceptor frame is in every stack.
- **A preview**, from `PlannedSteps` + `DeclaredSteps`, through the `PreviewRender` model — including
  which `[BobcatStep]` helper each step binds to.

## `And` and `But` continue a narrative; they cannot start one

A comment opening with `And`/`But` where no step has opened the narrative in that test is **ordinary
prose**, reported as `BOBCAT029` (info) so an author who meant a step is never left wondering. English
sentences begin "And …" constantly, and this file's own `FactSpecs` had `// And a false one fails its
step` become a step that wrapped the two real steps under a narrative row nobody wrote.

The honest limit, pinned by
`Bobcat.Projected.Acceptance.Tests/MarkerCommentTighteningTests.once_a_narrative_is_open_an_and_comment_is_a_step_whatever_it_says`:
inside a test that is already narrating, `And …` is taken at its word. There is no way to tell that
sentence from a step, and guessing from its wording would be worse than a rule an author can learn.

## Two smaller decisions worth a second opinion

- **Scenarios are ordered alphabetically within a feature.** Finish order changes between runs of an
  unchanged suite, which makes two reports impossible to diff. Source order would be better and is a
  compile-time fact the generator knows and does not currently register.
- **A scenario that declares no steps renders as `OK`** with a dim "declares no steps" line. Bobcat
  treats a zero-step scenario as a pending-specification hotspot everywhere else, so `PENDING` may be
  the more honest word — as it may be for a narrated scenario whose every step is `○`.
