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

# the test runner's own output only
./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples

# ...plus the rendered specifications
BOBCAT_SPEC_CONSOLE=1 ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples
```

```bash
# ...or the specifications WITHOUT running them — the projected lane's `bobcat preview`
BOBCAT_SPEC_PREVIEW=1 ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples --list-tests
```

**Seventeen of the thirty-two specifications fail on purpose**, which is why `IsTestProject` is `false`:
`dotnet test` never collects this project, and a red run here is the samples working.

## This pass covers Sentence and Fact grammars

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
| — | `NarratedSpecs.*` | the marker-comment style, for contrast |

Not yet: Sets (`SetVerification`), `create_object`/`verify_object`, `ApiFixture`, `ModelFixture`,
selection lists, Paragraphs (deliberately out of scope).

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

**One grammar body serves both lanes.** `TheRosterIs(StepTable roster)` is the same method a
`.feature` file binds to; the document supplies the table there and the caller supplies it here, and
nothing in the grammar knows which. Both lanes then render the same grid — the table becomes cells
carrying a `RowIndex` plus the column order, which is exactly what a Gherkin `[Table]` step produces,
so `SetVerificationRender.FromCells` is one fold for both.

The two alternatives were worse. Calling a row helper once per row renders as N steps and loses the
grid, which is the report the Gherkin lane just stopped producing. A collection-of-tuples argument
(`void Sum((int x, int y, int sum)[] rows)`) grids up fine but is a second signature written for the
C# lane beside the one the document binds to, so a change to the vocabulary has to be made twice.

A markdown table pastes in unchanged — the alignment row is recognised and dropped, outer pipes are
optional, cells are trimmed — so the table in the specification, the table in the pull request and
the table in the test are the same text. What is deliberately not supported is markdown's escaping
and inline formatting: a cell is the text between pipes, because that is what a Gherkin cell is, and
two rules for reading a cell is how the lanes would drift.

A **table of objects** is `TableRunner.BuildRows<T>(table)` — Storyteller's `CreateNewObject<T>`,
with the same cell conversion the Gherkin lane uses, so `TODAY+30` is a date and a field the record
defaults need not appear in the table at all. Its "before all rows" and "after all rows" hooks are the
lines either side of the call; Storyteller needed hooks because the table was declared rather than
called. On a grammar that inherits `Fixture`, `BuildRows<T>` and `RunTable(nameof(...), table)` are
there directly.

A **decision table** works the same way, with the grammar reporting one cell per row —
`SpecAssert.Check(name, actual, expected, rowIndex: i)` — so the grid carries a verdict per row. The
comparison supersedes the value the literal wrote for that column, which is why a wrong row reads
`expected '5', got '4'` in the `sum` column rather than echoing the `5` the test typed.

## One attribute family, two expression syntaxes

`[Given]`, `[When]`, `[Then]`, `[Check]` and the keywordless `[Step]` all derive from one
`StepAttribute`, and every one of them works in **both** lanes: matched against a `.feature` file on a
fixture, and intercepted at the call site when a test calls the method directly. `[BobcatStep]` is the
legacy spelling of the same thing.

Each expression may be a **Cucumber expression** (`{int}`, `{string}`, the `{aggregate}`/`{event}` type
words, raw regex) or a **Storyteller `[FormatAs]` template** naming parameters (`{sum}`). Decided per
placeholder, built-in word first, so nothing that compiled before means anything different — and the
two mix freely in one expression.

Facts are Storyteller's shape, unchanged: a `bool`-returning step whose answer is the verdict, with no
`[Check]` and nothing reported by hand. `Task<bool>` works the same way and carries a real duration.

A **named tuple** return is compared element by element — the async-safe replacement for `out`
parameters, which an `async` method cannot have. See `Bobcat.Acceptance.Tests/NamedTemplatesFixture`;
it belongs in the Gherkin lane, because the expected cells come from the document and BOBCAT027
correctly refuses them at a C# call site.

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

- **A keyword.** Bobcat's model is Gherkin-shaped, so a step says whether it arranges, acts or
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

`SpecAssert.Check` runs the same `CellCheck` comparison the Gherkin lane's return-value verification
runs, so a projected check and a `.feature` check on the same value agree and render identically.

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

Two defects, both fixed here, and three gaps still open.

**Fixed — a synchronous step that threw rendered green.** The emitted interceptor wrapped a
synchronous call in `using (step)`; disposal alone means "the step ended", not "the step failed", so
the exception never reached the step. The asynchronous path never had it, because
`MarkerStepRuntime.Track` catches and reports. Pinned by
`Bobcat.Acceptance.Tests/SynchronousStepFailureTests`. It hid because the scenario was still red —
the only symptom was that the failing line was the one marked `✓`, and nothing rendered a projected
specification locally for anyone to read.

**Fixed — `Succeeded with Rights: 0, Wrongs: 0` printed in green under a `FAILED` heading.** A
specification can fail with every count at zero, and `Counts.Succeeded` then says *Succeeded*. The
verdict word now comes from the caller that knows it.

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

**Gap 3 — a projected step has no logs and no diagnostics.** `StepRender` renders both for the
Gherkin lane; `ScenarioRecorder.RecordedStep` carries neither, so Storyteller's
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
- **A preview**, from `PlannedSteps` + `DeclaredSteps`, through the same `PreviewRender` model the
  Gherkin lane uses — including which `[BobcatStep]` helper each step binds to.

## `And` and `But` continue a narrative; they cannot start one

A comment opening with `And`/`But` where no step has opened the narrative in that test is **ordinary
prose**, reported as `BOBCAT029` (info) so an author who meant a step is never left wondering. English
sentences begin "And …" constantly, and this file's own `FactSpecs` had `// And a false one fails its
step` become a step that wrapped the two real steps under a narrative row nobody wrote.

The honest limit, pinned by
`Bobcat.Acceptance.Tests/MarkerCommentTighteningTests.once_a_narrative_is_open_an_and_comment_is_a_step_whatever_it_says`:
inside a test that is already narrating, `And …` is taken at its word. There is no way to tell that
sentence from a step, and guessing from its wording would be worse than a rule an author can learn.

## Two smaller decisions worth a second opinion

- **Scenarios are ordered alphabetically within a feature.** Finish order changes between runs of an
  unchanged suite, which makes two reports impossible to diff. Source order would be better and is a
  compile-time fact the generator knows and does not currently register.
- **A scenario that declares no steps renders as `OK`** with a dim "declares no steps" line. Bobcat
  treats a zero-step scenario as a pending-specification hotspot everywhere else, so `PENDING` may be
  the more honest word — as it may be for a narrated scenario whose every step is `○`.
