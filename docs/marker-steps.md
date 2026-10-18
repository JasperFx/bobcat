# Writing Specifications

A Bobcat specification is an ordinary test in the runner you already use -- xUnit v3, TUnit, NUnit
or MSTest -- that also reports itself as a specification: ordered steps with their own verdicts, a
`{Feature}/{Scenario}` identity, [a rendered specification on the console](#reading-the-specification-the-run-produced),
tables and sets that grid up, and live progress [on the wire a run publishes](monitor-design.md).

The steps arrive three ways, and they compose freely in one test:

| | |
|---|---|
| **A scenario vocabulary** | `CritterStackSpec` and WolverineFx.Bobcat's `WolverineSpec` -- a base class whose members *are* the steps: `GivenEvents`, `WhenReceived`, `ThenEvents` and the rest. See [Bobcat with Marten, Polecat and Fisher](integrations/marten.md) and [Bobcat with Wolverine](integrations/wolverine.md) |
| **Decorated step helpers** | `[Given]`, `[When]`, `[Then]`, `[Check]` or `[Step]` on a method you write. Every call to it renders as that step, with its own timing and verdict |
| **Marker comments** | `// Given …`, `// When …`, `// Then …` in a test body, declaring the narrative of a test that calls no decorated helper at all |

<!-- JEREMY -->
The constraint that shaped all three: a large existing suite has to be able to adopt this **one class
at a time**, without a base class, a signature change, or a rewrite. Marten's `DaemonTests` was the
first real subject ([JasperFx/marten#5363](https://github.com/JasperFx/marten/pull/5363)) --
eighteen tests, no test body edited.

## What you have to add

| | |
|---|---|
| Packages | `Bobcat`, `Bobcat.Generators`, and the adapter for your runner |
| A runner adapter | `Bobcat.Xunit`, `Bobcat.TUnit`, `Bobcat.NUnit` or `Bobcat.MSTest` |

That is the whole list. `Bobcat.Generators` brings the interceptor opt-in with it, so there is no
csproj line to remember -- and no sibling project that silently needed the same one.

Then mark the class:

```csharp
using Bobcat.Xunit;      // or Bobcat.TUnit, Bobcat.NUnit, Bobcat.MSTest

[BobcatFeature("Async daemon")]
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

`[BobcatFeature]` names the feature; leave the title out and the class name, prettified, is used.
**On xUnit that is all it takes**: the `Bobcat.Xunit` package applies an assembly-level hook,
`[assembly: RecordBobcatFeatures]`, that opens a scenario around every test in a `[BobcatFeature]`
class. Opt out with `<BobcatRecordFeatures>false</BobcatRecordFeatures>`. An in-repo
`ProjectReference` doesn't consume a package's build assets, so a project referencing `Bobcat.Xunit`
that way writes the assembly attribute itself.

**On TUnit, NUnit and MSTest**, put `[BobcatScenario]` beside `[BobcatFeature]` -- on the class, or on
a single method when only part of a class should project. Without it the steps register at compile
time but no scenario is ever opened, so nothing records or publishes. When the class also binds an
Event Model slice, [BOBCAT028](diagnostics.md#bobcat028) says so at build time.

Every adapter opens a scenario around each test and closes it with **the verdict the runner
reported** -- a failing test is published as a failure, and a skipped one is withdrawn rather than
reported as anything.

**Use the shipped adapter rather than writing your own.** Bobcat deliberately shipped no adapter at
first, on the theory that forty lines were cheaper than guessing at a runner. That was wrong: those
forty lines carry four things only Bobcat knows, and getting any of them wrong leaves a green suite
looking exactly like a correct one. Marten's hand-rolled copy got all four wrong. It never set the
verdict, so every scenario it ever published was a `CleanPass`. It minted its own run id and dropped
`BOBCAT_RUN_TAG`, so its evidence could not be attributed to whatever asked for the run. It published
a `RunStarted` it might not own, and never a `RunFinished`. And its interceptor opt-in was a csproj
line that a sibling project also needed (forgetting it is now [BOBCAT037](diagnostics.md#bobcat037)).

**xUnit v2 has no adapter and is not simply waiting for one.** v2's `BeforeAfterTestAttribute` is
`Before(MethodInfo)` / `After(MethodInfo)` with no test context anywhere, so there is no verdict to
read at that point at all. v3, TUnit, NUnit and MSTest all hand the result over at the end of a test,
which is what makes their adapters small. (MSTest's arrives in a global cleanup hook rather than an
attribute, so [its adapter](mstest.md) compiles that hook into the test project.)

## Step helpers

A step attribute on a method -- often a shared helper -- goes the other way from a comment. Decorate
it once and *every* test that already calls it renders that step:

```csharp
[Given("the events are published on {threads} threads")]
internal Task PublishMultiThreaded(int threads) => …
```

`{threads}` is filled in from the call site, so one attribute renders `PublishMultiThreaded(3)` as
"Given the events are published on 3 threads".

`[Given]`, `[When]`, `[Then]`, `[Check]` and the keywordless `[Step]` all derive from one
`StepAttribute`. `[BobcatStep]` is an older spelling of the same thing and still works; its
`Keyword = "Given"` property is what `[Given]` says in one word.

An expression is a **named template**. Each `{placeholder}`
names a **parameter** of the method and is filled from that argument at the call site: `"Adding {x}
to {y} should equal {sum}"` over `double Adding(double x, double y, double sum)`.

**Placeholders bind by name only.** Cucumber type captures (`{int}`, `{string}`, `{word}`, optional
text, raw regex) are not supported: they only ever worked by matching Gherkin text against the
expression, and that lane is gone (#471). `[Then("the message ends {word} on attempt {int}")]` over
`(string outcome, int attempt)` fills nothing -- it is a [BOBCAT027](diagnostics.md#bobcat027)
warning and renders literally.
Write `[Then("the message ends {outcome} on attempt {attempt}")]` instead. A name such as
`{aggregate}` or `{event}` works when a parameter has that name (`Type aggregate`, `Type @event`);
a `System.Type` argument renders as the type's name.

**Keywordless is a real choice, not a default.** Some steps read as prose, and "Multiply by 3 then
add 4" is not a Given, a When or a Then:

<<< ../src/Bobcat.Xunit.Samples/Grammars/SentenceGrammar.cs#keywordless-step{cs}

An empty keyword renders no label, and it is never promoted to `And`, because `And` is a word the
author never wrote.

### A decorated helper cannot be `protected`

A C# interceptor must be an extension method, and an extension method cannot see a `protected`
member. So every decorated helper has to be `internal` or `public` (`protected internal` works too).
This is a language rule rather than a Bobcat design choice, and it is the one real cost of step
helpers -- widening eight helpers on `DaemonContext` is exactly what Marten's PR did. A `protected` or
`private` helper, or one on a type the assembly can't reach, is [BOBCAT039](diagnostics.md#bobcat039)
at the helper, and its calls record no step. Marker comments have no such constraint.

### A placeholder binds from the value, not from the syntax (issue #339)

A literal argument is substituted at build time. Everything else binds **at execution time**, from
the value the helper was actually called with -- which matters because a typed vocabulary has almost
no literals in it:

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

How a value reads:

| value | renders as |
|---|---|
| `Type` | its short name -- `Appointment` |
| `string`, number, `bool`, `Guid`, enum, date/time | the value, culture-invariant |
| a sequence | its items, comma-joined, capped at five |
| anything else | its **type** name -- `new ConfirmAppointment(id)` is `ConfirmAppointment` |
| `null`, an empty string, an empty sequence | nothing: `{name}` stays as written |

A step's text is a sentence on a canvas, which is why an object renders as its type rather than its
`ToString()` -- the data belongs in a table, not in the prose. And a value with nothing to say leaves
the placeholder standing, because a reader can see that something did not resolve, where a blank or
the word "null" would be believed.

**A placeholder that names no parameter is a warning** -- BOBCAT027, once per helper -- so a
template typo can't render as `{thread}` forever with nothing reported.

## Marker comments

Marker comments declare the steps of one test:

```csharp
[BobcatFeature("Async daemon")]
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
`Given`, `When`, `Then`, `And` or `But` as a whole word -- everything else stays invisible, which is
the property that makes this usable on a real suite full of explanatory comments. Marker comments
reach the runtime through a module initializer, so an assembly with no marked class gains no
initializer and no startup cost.

**A marker may wrap onto more `//` lines (bobcat#687).** The comment lines directly below a marker
are part of its sentence, joined with spaces, up to a blank line, a line of code, or a line that is
a marker of its own (`// And …`, `// * …`, `// Note: …`):

```csharp
// Then every event at or below the committed mark is in a document already; nothing past the
// failing event is in one
await CheckDocuments();
```

renders as one step with the whole sentence. To keep an ordinary comment out of the step, put a
blank line between them or move it below the code.

The two compose. Comments give a test its narrative; decorated helpers give real per-step timing
across every test in the suite that touches them.

### A step need not be a keyword -- `// *` (issue #324)

Bobcat's rendering does not depend on Given / When / Then, and should not. The inspiration is
ThoughtWorks' **Gauge**, whose specifications are bulleted sentences with no keyword vocabulary at
all:

```csharp
[Fact]
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

That renders as three steps, the first two carrying no keyword at all. **The bullet is required**
because most comments in a test body are not steps. `*` is one character of opt-in, unmistakably
deliberate, and the same character Gauge uses. `// just explaining the next line` stays a comment.

### They are different steps, and they nest (issue #305)

When a decorated helper is called inside a comment-declared region, **both** describe the same
stretch of the test -- and the answer is not a precedence rule, because they are not the same kind of
claim. The comment is the author's sentence about *this* test. The attribute reports an *operation*,
wherever it is called from. So the comment is the row, and the helper renders underneath it with its
own keyword and its own verdict:

```
Given the events are published                    52ms
    Given the events are published on 3 threads   40ms
    Given the events are flushed                  12ms
When the projection daemon is running             declared
```

A helper called where no comment has been written yet -- before the first marker, or from a method
that is not a test -- belongs to no sentence and renders on its own.

**A helper of another keyword ends the region (bobcat#682).** Under `// Given the AppHost is running`,
a `[When]` helper is not part of the Given, so the region ends there: that helper, and every call after it
up to the next marker comment, renders on its own rather than indented under a sentence of a different
keyword.

```csharp
// Given the AppHost is running
await StartHost();                  // [Given] -- nested under the comment
await WhenTheAboutPageIsRead();     // [When]  -- ends the region, renders on its own
ThenTheConsoleAnswers(200);         // [Then]  -- on its own too
```

An `And`/`But` comment carries the keyword before it, so a `[When]` helper under `// And …` after a
`// When …` still nests. A keywordless helper (`[Step]`), a bulleted `// * …` comment and a projected
assertion end nothing.

### What a declared step can say about itself (issue #304)

A declared step reports **the work observed inside it**: the steps that ran under it, their verdicts,
and the sum of their durations. It reports nothing else, and a region with no decorated helper inside
it stays blank -- marked `declared`, with no duration and no verdict. Nothing observed it, and a
number borrowed from its neighbours would be exactly the inference this feature promises not to make.

The attribution is decided **by the generator, from the call site's line**, against the comments in
the same method -- not from a stack trace, not from a clock. An interceptor is generated per call
site, so at build time which sentence a call sits under is an exact fact, and at runtime it would be
a guess.

### Declared is not executed

Two different things, kept apart on purpose:

- **Declared steps** are what the test *says* it does, read from marker comments at compile time and
  known before a line of it runs. That is what lets a scenario announce "step 2 of 4" up front.
- **Recorded steps** are what actually ran, with a duration and a verdict. They come from step
  helpers and from the scenario vocabulary.

`DeclaredSteps.For("{Feature}/{Scenario}")` reads the first at runtime; every `DeclaredStep` carries
the source line its comment came from.

## Reading the specification the run produced

Everything a specification records leaves the process over the monitor wire, but the rendering is
the whole point of writing specifications, so **a run also prints its specifications itself**,
grouped by feature:

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
```

It is on **when a terminal is attached and nothing is listening on the wire** -- precisely the case
where the run would otherwise say nothing at all. A redirected stream belongs to whoever redirected it
(a test platform, a CI runner, a pipe), and a console on the wire already renders these
specifications, so printing them twice would read as two reports of one run.

| | |
|---|---|
| `BOBCAT_SPEC_CONSOLE=1` | print anyway -- into a captured stream, with a console listening |
| `BOBCAT_SPEC_CONSOLE=0` | stay silent -- in a terminal, with nothing listening |
| unset | the default above decides |
| `BOBCAT_SPEC_CONSOLE_FILE=spec.txt` | write the report to that file instead of standard output (colour, laid out at `BOBCAT_SPEC_CONSOLE_WIDTH` columns, default 140) |

The variable is deliberately a **tri-state**. Collapsed to a bool, "somebody said no" and "nobody
said" are the same value, and `BOBCAT_SPEC_CONSOLE=0` would have printed anyway.

The report is written as the process exits. Into a captured stream it goes through standard output
itself rather than through whatever `Console.Out` the process holds by then, because a test, a logger
or the test platform may have swapped that writer and closed it -- which used to lose the whole report
without a word while a terminal printed it fine (bobcat#676). If the write still fails, a line on
standard error says why. **`BOBCAT_SPEC_CONSOLE_FILE` is the route to rely on in CI**: it depends on
no stream at all, keeps the report out of the test platform's own output, and is what the `bobcat`
tool reads.

`BOBCAT_SPEC_PREVIEW=1` is the other half -- every specification **without** its results, including
which helper each step binds to. Pair it with the platform's own `--list-tests` to preview without
executing anything at all: the plan is registered by a module initializer, so it is known before a
single test runs.

```bash
BOBCAT_SPEC_PREVIEW=1 ./MySpecs --list-tests
```

A preview shows marker comments, `[Given]`/`[When]`/`[Then]` helpers, and **scenario vocabulary calls**
(bobcat#625) -- `CritterStackSpec`'s and WolverineFx.Bobcat's `GivenEvents`, `WhenReceived`,
`WhenPosted`, `ThenDocument` and the rest, or a project's own scenario methods carrying
`[EventModelRole]`/`[EventModelTrigger]`. A vocabulary step writes its sentence from its arguments when
it runs, so its preview is the method name after the keyword plus the **types** the call names, never
its values:

```
  deposit funds
      ○ Given events Account, AccountOpened
        ↳ CritterStackSpec.GivenEvents
      ○ When  posted DepositFunds
        ↳ BankAccountSpec.WhenPosted
      ○ Then  events FundsDeposited
        ↳ CritterStackSpec.ThenEvents
```

A vocabulary call is only recognised in the test method's own body; one made inside a helper method the
test calls is not shown (mark the helper `[Given]`/`[When]`/`[Then]` if it should be).

Both are written at **process exit**, not after the last test -- nothing here knows which test is the
last one, and the runner does not say. `ProjectedSpecConsole.Captured` is the seam for a consumer that
would rather render or assert on the specifications itself, and `JsonRenderer.RenderScenario` turns
one into the agent-facing JSON. For a record the run leaves behind, see [the results
file](bobcat-tool.md#results-file) and [the HTML results page](bobcat-tool.md#results-page).

Scenarios are ordered **alphabetically within a feature**, because a test runner is free to run tests
in any order and in parallel, so finish order would make two reports of an unchanged suite impossible
to diff. A scenario that declares no steps renders as `OK` with a dim "declares no steps" line.

### Where the time went (issue #466) {#timing}

Every scenario carries two clocks, and they are not the same number:

- **`Recording.WallClockMs`** is the whole bracket, from the adapter opening the scenario to closing
  it. That is the `Duration` the report prints, the `durationMs` in the scenario JSON, and
  `DurationMs` on `scenario_finished`.
- **`Recording.StepsMs`** is what the top-level steps cost, added up. It shows up beside the duration
  as `Duration: 900ms (steps 40ms)`, as `stepsMs` in the JSON, and as `StepsMs` on
  `scenario_finished` (optional and additive, so an older reader just ignores it).

The difference is time no step owns: test-body code between two helpers, an assertion written with
your own library, a wait that isn't a step. A test that spends nine seconds outside its steps no
longer reads as a fast one.

The exit-time report then ends with a timing summary, built by `SuiteTiming.For(recordings)`:

```
  Timing -- 1.1s measured across 3 scenario(s)
    • slow outside its steps 1.0s (93% of measured time) -- steps 50ms, outside steps 950ms
    step the total is {number} cost 80ms across 2 occurrence(s)
    lifecycle not measured -- fixture setup belongs to the test runner in the projected lane
    ⏳ 880ms outside any step (unattributed) in slow outside its steps between 'a quick arrange' and 'a quick check'
  ⚠ 1 scenario(s) ran steps but asserted nothing
    • Wallet/a command is sent
```

That is three facts. **Per-step cost** across the suite, grouped by step text with the argument values
folded out, so "the total is 7" and "the total is 9" are one line. **Gaps** between the steps, largest
first. And **scenarios that assert nothing** -- recorded steps, but no `Then` step (an `And` after a
`Then` counts), no comparison cell, no failed assertion, and no `// Then` marker comment. The comment
counts because `// Then the total is 7` over a Shouldly line really is an assertion; Bobcat just can't
see it as a step. `JsonRenderer.RenderTiming(timing)` gives you the same figures as JSON.

**What isn't measured, on purpose: the lifecycle.** Your test runner owns the lifecycle -- the class
constructor, `IAsyncLifetime`, class and assembly fixtures, TUnit's hooks -- and it runs *outside* the
adapter's bracket. Bobcat doesn't own that lifecycle, so it doesn't measure it, and the report says so
instead of guessing:

- `SuiteTiming.LifecycleMeasured` is `false`, the suite's `Lifecycle` list is empty, and each
  scenario's `Lifecycle` is `null`.
- The console prints "lifecycle not measured", never "lifecycle 0ms".
- The JSON carries `"lifecycleMeasured": false` and leaves out `lifecycle` and `lifecycleMs`.
- A gap is time outside any step and nothing more is known about it, so it's labelled *unattributed*.
  The neighbouring steps tell you where it sat, not what it was.

If your suite's cost really is in fixture setup, look at the runner's own per-test timings, or the
supervisor's `RunTiming` for a run split across processes.

## Checks that gather instead of throwing

An assertion library throws, and a throw ends the test method -- so a specification could only ever
show its *first* disagreement, with every later step unrun.

`SpecAssert` is how a step reports a comparison without ending the test:

<<< ../src/Bobcat.Xunit.Samples/Grammars/CalculatorGrammar.cs#then-value-should-be{cs}

| | |
|---|---|
| `SpecAssert.Check(name, actual, expected)` | one expected/actual cell, named for the placeholder it belongs to -- so the verdict renders *in* the sentence where the value sits. Takes `rowIndex:` for a table row, and `options:` (a `CheckOptions`) for a comparison of your own |
| `SpecAssert.Check(name, actual, expected, tolerance: 0.1)` | the same cell, compared within an absolute tolerance, carrying a `±0.1` note |
| `SpecAssert.Fact(condition, because)` | a boolean verdict with no pair of values |
| `SpecAssert.Fail(message)` | a failure the step describes itself |
| `await SpecAssert.EventuallyAsync(...)` | poll until a value matches, a condition comes true, or an action stops throwing. See [below](#waiting-for-an-answer-that-arrives-later) |
| `SpecAssert.Gather(action)` | run an assertion that *does* throw -- Shouldly, xUnit, NUnit -- and record it as this step's failure instead of ending the test. **Only an assertion** is gathered; anything else is rethrown unchanged, because swallowing a `NullReferenceException` would turn a broken test into a merely red one |

The scenario vocabularies report the same way: every `Then…` on `CritterStackSpec` and `WolverineSpec`
gathers its wrong on its own step, so one specification reports every disagreement it has.

**The runner still has to be told**, or a red specification would be reported as a green test. The
gathered wrongs become the test's verdict once, at the end, when the adapter's after-test hook throws
them:

```
failed CalculatorSpecs.asserting_values
  SpecAssertionException : 1 specification step failed:
    And For X=4 and Y=4, the Sum should be 6 and the Product should be 8 => Sum: expected '6', got '8'
```

**A wrong with no step open still fails the scenario.** A `SpecAssert` only has a step to report to
when it runs inside an intercepted helper. Call one from a helper that was *not* intercepted -- the
project never opted into Bobcat's interceptors, or never referenced the generator -- and there is no
step, and that wrong used to vanish, so the spec passed. Now it's kept on the scenario and fails it,
with a hint about why:

```
failed AccountSpecs.a_deposit_is_credited
  SpecAssertionException : 1 SpecAssert wrong was reported with no step open:
    balance: expected '150', got '100'
    If these came from a [Given]/[When]/[Then] helper, it was not intercepted: ...
```

A *passing* check outside a step still records nothing, the way it always did. A helper gets called
from setup code too, and a check that holds there has nothing worth saying.

A `bool`-returning step is the other shape: the answer *is* the verdict,
with no `[Check]` and nothing reported by hand. `Task<bool>` works the same way and carries a real
duration.

### Waiting for an answer that arrives later

A C# test calls a helper itself, so there's no generated code around the call to retry it. The poll
goes inside the helper:

```csharp
[Then("the outstanding count becomes {count}")]
internal Task OutstandingBecomes(int count)
    => SpecAssert.EventuallyAsync("count", () => _queue.Outstanding, count,
        timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(50));
```

| | |
|---|---|
| `EventuallyAsync(name, () => actual, expected, ...)` | ask for the value until it matches. Takes `options:` (a tolerance among them) and `rowIndex:` like `Check`, and there's a `Func<Task<T>>` form for a value that has to be fetched |
| `EventuallyAsync(() => condition, ..., because:)` | a `Fact` that's allowed to take a while |
| `EventuallyAsync(() => { ... }, ...)` | run an action until it stops throwing |

The timeout defaults to 5 seconds (`SpecAssert.DefaultEventuallyTimeout`) and the interval to 100ms
(`DefaultEventuallyInterval`). Every form returns whether it got there, so a helper can branch on it.

**One cell, not one per attempt.** The step gets a single cell when the poll is over: the passing
comparison with a `converged after 120ms (3 polls)` note, or the last disagreement with a
`timed out after 5000ms @50ms (100 attempts)` note, so the reader sees the value the system was
actually stuck on. A timeout is a wrong, gathered like any other, not an exception.

An exception inside the window is "not ready yet", and the last one is named in the timeout note.
Cancellation, `SpecCriticalException` and `SpecCatastrophicException` aren't retried, because all
three mean stop.

There is no `[WaitFor]` or `[Approx]` attribute any more: both were Gherkin-lane modifiers and went
with it in 0.50.0, along with BOBCAT035, which warned that they did nothing on a helper a C# test
calls. The poll goes in the helper, as above, and a tolerance goes in `options:`.

### Saying what the system did -- `SpecLog.Write` (bobcat#547) {#step-logs}

When a spec goes red the next question is "what did the system do?". A step helper answers it
with `SpecLog.Write`:

```csharp
[When("the order is placed")]
internal async Task PlaceOrder()
{
    var response = await _client.PostAsJsonAsync("/orders", _order);
    SpecLog.Write($"POST /orders -> {(int)response.StatusCode}");
}
```

- **The line goes on the step in progress**, the innermost open one, and renders under that step in
  the console, the JSON report (`logs` on the step), the runner's own test output, and the monitor
  wire (`scenario_finished.logs`, keyed by step id). A line written with no step open goes on the
  scenario and renders after the steps under `Logs:`. With no scenario open it does nothing.
- **Latched like reports.** The lines are always recorded but only written out for a **failed**
  scenario. A passing one shows none unless the run is verbose (`bobcat run --verbose`, or
  `BOBCAT_VERBOSE_REPORTS=1`). So a green
  spec showing no logs does *not* mean `SpecLog` did nothing.
- **Capped at 100 lines per step** (`StepLog.MaxLines`); the rest are counted and every surface
  says `…and N more lines`, never a silent truncation.
- **Writing can never fail a test.** It is static and `AsyncLocal`-backed like `SpecAssert`, so a
  vocabulary in another package can call it without being handed anything, and parallel tests never
  log into each other. Your application's own `ILogger` output is **not** captured; that is still
  post-1.0.

## When the environment is gone, stop -- `SpecCatastrophicException` (bobcat#539) {#environment-lost}

A failure comes in one of three sizes:

| | What it means | What happens |
|---|---|---|
| a wrong (`SpecAssert`, a `[Check]` returning false, `SpecAssertionException`) | the system gave the wrong answer | the scenario carries on and gathers every wrong |
| `SpecCriticalException`, or any other exception | this scenario can't go on | the scenario stops; the next one runs |
| **`SpecCatastrophicException`** | **nothing after this can pass** | **every scenario that opens afterwards in the process fails without running** |

### Report everything, then stop -- one step, two sizes (bobcat#669) {#gather-then-stop}

A wait step that gives up can do both: gather a wrong for each thing it waited for, so every
disagreement is reported, then throw `SpecCriticalException` because the rest of the scenario
depends on it. That's the intended pattern:

```csharp
SpecAssert.Check("agents running on more than one node", duplicated, "none");   // fails
SpecAssert.Check("agents not running anywhere", missing, "none");              // passes
throw new SpecCriticalException("the agents did not settle; the rest of the scenario depends on it");
```

The step's status is `error`, and the counts carry both: each check is a right or a wrong, and the
exception is one error. Here that's `Rights: 1, Wrongs: 1, Errors: 1` for the step. So `errors`
tells "the scenario couldn't go on" apart from "the system gave a wrong answer" even when one step
says both. An *assertion* exception beside its own failed cell (a projected Shouldly assertion
records both) is still one wrong, not two.

### An environment that's gone

The last row is for an environment that's gone. The usual case is a Playwright suite whose
application won't start. Every test needs the app, so every test would wait out its own timeout to
find that out, and the run ends with hundreds of timeouts and the real cause buried somewhere in
the first one. Throw `SpecCatastrophicException` where you start the app instead:

```csharp
public sealed class RunningApp : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        try
        {
            await _app.StartAsync();
            await _page.GotoAsync(_app.BaseUrl);
        }
        catch (Exception e)
        {
            throw new SpecCatastrophicException($"the app did not start: {e.Message}", e);
        }
    }
    // ...
}
```

The test that meets it reports the real error, stack trace included. Every scenario after it fails
at once, without running its body:

```text
Bobcat.Engine.SpecEnvironmentLostException : Environment lost: the app did not start: connection refused (this scenario did not run)
```

A plain `dotnet test` then fails fast with one cause. This works in xUnit, TUnit, NUnit and MSTest.

- **It fires from setup too:** a class or collection fixture, `IAsyncLifetime`, `[OneTimeSetUp]`,
  `[ClassInitialize]`, `[Before(Class)]` or host startup. That's where an app that won't start
  actually fails, before any scenario has opened. Thrown in setup, the message is
  `Environment lost: <message>`. Thrown inside a scenario, it names that scenario:
  `Environment lost in Checkout/pays by card: <message>`.
- **Throwing it is what counts.** Bobcat watches for the exception being thrown anywhere in the
  process, so it still counts if something catches it afterwards. Don't throw one to test something;
  throw it when the environment really is lost.
- **Only scenarios that open afterwards are halted.** A scenario already running in parallel keeps
  its own result. The first catastrophic failure is the one every halted scenario names.
- **A fixture per class still runs for every class.** xUnit creates class B's fixture before any of
  its tests can be halted, so a per-class app fixture would try to start the app again. Call
  `SpecEnvironment.ThrowIfLost()` first to skip the attempt, or start the app once in an assembly or
  collection fixture.
- **Never retried.** `SpecEnvironmentLostException` is a `SpecCatastrophicException`, so a `Retry`
  trait doesn't retry it.
- **Under the supervisor** the halt is per worker process. The supervisor aborts the whole run on
  either exception ([a `SpecCatastrophicException` stops a supervised
  run](parallel-ready-suites.md#a-speccatastrophicexception-stops-a-supervised-run)).

## Failure evidence: attachments and screenshots (issue #571) {#attachments}

A red step often needs more than its message: the page it was looking at, the response body, a log.
`SpecAttachment` puts bytes on the **step in progress**, or on the scenario when called between steps:

```csharp
SpecAttachment.Add("response.json", bytes, "application/json");
SpecAttachment.AddText("console.log", consoleOutput);
SpecAttachment.AddFile(screenshotPath);          // read now; media type from the extension
```

For evidence nobody should have to ask for in every spec, register an `IFailureCapture` **once** --
a Playwright base class's static constructor, an assembly fixture, a module initializer. It is called
for every step that goes `failed` or `error` (the innermost one, not each parent the failure bubbles
through) and returns attachments that hang on that step:

```csharp
public sealed class ScreenshotOnFailure : IFailureCapture
{
    public async Task<IReadOnlyList<CapturedAttachment>> CaptureAsync(FailedStep step, CancellationToken cancellation)
    {
        if (BrowserSpec.CurrentPage is not { } page) return [];   // the test's own AsyncLocal flows in
        return [new CapturedAttachment("screenshot.png", await page.ScreenshotAsync(), "image/png")];
    }
}

SpecAttachment.RegisterFailureCapture(new ScreenshotOnFailure());
```

- **It can never fail a test.** A capture that throws, or runs past `SpecAttachment.CaptureTimeout`
  (10 s), is logged to the test's output -- `Bobcat: failure capture X threw on '…' and was skipped` --
  and stepped over; the step keeps its own verdict.
- **There are no general `BeforeStep`/`AfterStep` hooks**, deliberately: this seam can only produce
  evidence, never behaviour hidden around steps.
- **Latched like reports.** A passing scenario keeps nothing unless the run is verbose
  (`--verbose`, `BOBCAT_VERBOSE_REPORTS=1`).
- **Where they go.** With a results directory (`BOBCAT_RESULTS`, `bobcat run --results`; see
  [the results file](bobcat-tool.md#results-file)) each is a
  file at `attachments/{runId}/{feature}_{scenario}/{nn}-{name}` beside the results file, whose step
  names it by that relative `path`. Without one, the test platform's own attachments: xUnit v3's
  `TestContext.AddAttachment`, a TUnit artifact, NUnit's `AddTestAttachment`, MSTest's
  `AddResultFile` -- so TRX and the IDE show them, numbered `01-…` so two of one name never collide.
- **On the wire, a reference only**: `ScenarioFinished.Attachments` carries name, media type, size,
  the absolute path (when written to a results directory) and the step id. Never the bytes.
- **Capped.** Over `SpecAttachment.MaxBytes` (10 MB) an attachment is kept as a reference marked
  `omitted: … is over the … cap`, never truncated; past `SpecAttachment.MaxPerScenario` (20) the rest
  are counted and render as "…and N more attachments not kept".

## Tables and sets from a C# test

A C# test has no trailing `|...|` block, so a table arrives as **pipe-delimited text** that
`StepTable` reads by an implicit conversion:

<<< ../src/Bobcat.Xunit.Samples/Grammars/RosterGrammar.cs#roster-is{cs}

<<< ../src/Bobcat.Xunit.Samples/Specs/TableSpecs.cs#table-step-call{cs}

The step renders as one grid, because a table becomes cells carrying a row index plus the column
order. A markdown table pastes in unchanged -- the alignment row is recognised and dropped, outer
pipes are optional, cells are trimmed -- so the table in the specification, the table in the pull
request and the table in the test are the same text. Markdown's escaping and inline formatting are
deliberately not supported: a cell is the text between pipes.

<!-- JEREMY -->
The two alternatives were both worse. Calling a row helper once per row renders as N steps and loses
the grid. A collection-of-tuples argument (`void Sum((int x, int y, int sum)[] rows)`) grids up fine
but reads as code rather than as a table.

The table family -- building objects from rows, running a method per row, decision tables, set
verification, and checking one object against one row -- is covered in full in
[Data Intensive Specifications](tutorials/data-intensive-specifications.md). In short:

| | |
|---|---|
| `TableRunner.BuildRows<T>(table)` | one `T` per row |
| `TableRunner.Run(this, nameof(rowMethod), table)` | a private method once per row, with the before/after envelope written in the step's own body. A returned value plus one unclaimed column, or an `out` parameter per output column, is a decision table |
| `SpecAssert.Check(column, actual, expected, rowIndex: i)` | a decision table reported row by row |
| `SetVerificationComparer.Verify(actual, expected, keyColumns: "Name")` | a set verification, matched by key, with `MISSING`, `EXTRA`, `FAIL` and `ORDER` rows |
| `PropertyCells.Verify(subject, expected)` | one object against one row, on only the columns the row names -- properties or fields, dotted paths included. `Verify(subject, table)` on a `CritterStackSpec` is the same check |

## Projecting the assertions you already wrote (opt-in)

With `<BobcatProjectAssertions>true</BobcatProjectAssertions>` in the project, an ordinary
statement-level Shouldly call inside a `[BobcatFeature]` test renders as a step -- no attribute, no
helper, nothing moved:

<<< ../src/Bobcat.Xunit.Samples/Specs/NarratedSpecs.cs#projected-assertions{cs}

```
  every assertion in a run is evaluated FAILED
    ✗ Then  the calculator agrees about its value
      ✓ Then  calculator.Value should be 3
      ✗ And   calculator.Value should be greater than 10
          ✗ calculator.Value: should be greater than '10', got '3'
      ✗ And   calculator.Value should be less than 2
          ✗ calculator.Value: should be less than '2', got '3'
      ✓ And   calculator.Value should be 3
```

A **run** of consecutive assertions is all evaluated before the next action, and the run's failures
are thrown at its end -- the point just before the next action, which would be operating on state the
assertions have already shown to be wrong. Everything after that renders as never reached. A plain
Shouldly test reports the first failure and leaves three blanks; here all four reach the report and
the test still fails, once.

**A cell says which comparison it made**, so a non-equality assertion cannot claim an equality it
never checked -- `should be greater than '10', got '3'`, never `expected '10', got '3'`.

**An assertion Bobcat cannot describe produces no cell at all.** The comparison comes from a closed
set, and `ShouldBeTrue`, `ShouldBeEquivalentTo`, `ShouldBeOfType` and `ShouldBeInRange` are not in
it -- their expectation is not a value to put beside an actual, so any row shape would state something
false. The step still renders, with its verdict and duration; only the cell is withheld.

**Only a statement-level call is projected.** `x.ShouldNotBeNull().Name.ShouldBe("a")` consumes the
first assertion's result, so gathering it would dereference null and report a
`NullReferenceException` instead of the assertion that failed. A call whose value is used is left
alone, which also means a fluent chain is safe by construction.

### Loops and branches

A projected assertion is a step **per call, not per line**. The generator plans one step for each
call site, and at run time every call records its own step, so the render shows what actually
happened. Two shapes catch people out:

- **An assertion inside a loop renders once per iteration.** A `ShouldBe` inside a `foreach` over 60
  events gives 60 steps, all reading `And x should be 1`. Nothing collapses them: each one really
  ran, and a failing iteration needs its own row to show which value was wrong.
- **An assertion in a branch that did not run renders as `○ … — not run`**, greyed out, even in a
  green scenario. The call site was planned and never reached, which is the same thing a step after a
  failure is. It is not a failure and does not change the verdict.

If that is not the specification you want to read, say it once over the whole collection, outside
the loop:

```csharp
// 60 steps, one per event
foreach (var e in events)
{
    e.Version.ShouldBe(1);
}

// one step
events.ShouldAllBe(e => e.Version == 1);
```

`ShouldAllBe` has no comparison Bobcat can put in a cell, so the step renders with its verdict and no
cell. Shouldly's own failure message, which lists the items that did not match, is still reported. A
branch is the same: put the assertion where it always runs, or leave the `not run` line to show that
the branch was not taken.

**Shouldly is the dialect Bobcat supports.** `IAssertionDialect` in the generator is the seam, and
FluentAssertions is the intended second. `Assert.*` is *not* planned: its subject is an argument whose
position differs per assertion, so a sentence built from one rule reads backwards.

## Listing and running one specification

A suite answers two questions -- *what do you specify*, and *run exactly this* -- because a monitor
only ever names identities and never a test framework's own filter:

```bash
BOBCAT_LIST_SPECS=/tmp/specs.json ./MySpecs --list-tests
```

The manifest it writes carries the lane (`projected`), the framework (`xunit`), and every
`{Feature}/{Scenario}` with the class and method it binds to -- which is the fact a framework's filter
needs and the identity cannot supply. Bobcat then translates an identity into
`--filter-method Ns.Class.method`. See [Spec Identities](spec-identities.md#listing-and-running-by-identity)
for the whole rule, [selecting by Event Model coordinates](bobcat-tool.md#selecting-by-coordinates)
for `bobcat run --slice`/`--chapter`/`--domain`/`--pattern`/`--trait`, and [The Resident
Runner](resident-runner.md) for the loop a console drives it from.

Two details matter if you are reading the manifest yourself. The binding is **recorded, not
re-derived** -- the identity is a one-way function of the names, and an explicit
`[BobcatFeature("…")]` title shares nothing with its class name. And it is emitted on a wider rule
than declared steps: what makes a test a specification is that a scenario is recorded around it, not
that it declared any steps. Keyed on declared steps, the first cut of this listed **8 of
`Bobcat.Xunit.Samples`' 41 specifications** while the other 33 rendered and published verdicts
perfectly well.

A test inherited from a shared base is listed, run and recorded **once per subclass**, under the
subclass's feature, with the steps its base declares. See
[Spec Identities](spec-identities.md#inherited-specs).

## What reaches the viewer

A scenario publishes `ScenarioStarted` (with the declared step count and, since #304, the
declared sentences themselves), `StepStarted` as each step **opens** -- carrying
`DeclaredStepNumber`, the sentence it ran under -- `StepFinished` with that step's own verdict, and
`ScenarioFinished`. Steps are announced when they open rather than when they end, because a
watcher looking at a run in flight needs the step that is currently taking the time -- which is
exactly the one that has not finished yet.

The narrative rides on `ScenarioStarted` rather than arriving as steps, and that is the wire
shape of "declared is not executed": publishing a declared step as a `StepStarted` would be
announcing that it ran.

With no viewer listening the publisher is null and the whole thing costs a few strings per test.

## Binding a specification to a slice -- `[BobcatSlice]` (issue #324) {#bobcatslice}

`[BobcatFeature]` makes a test *readable*. It said nothing about **which slice the test is
evidence for**, so the slice stayed unbound on the Event Model however green the test was, and no
spec-identity gate could join it. `[BobcatSlice]` closes that:

```csharp
[BobcatFeature("Booking appointments")]
[BobcatSlice(SliceType = typeof(ConfirmAppointment), Domain = "Scheduling", Chapter = "BookingAppointments")]
public class BookingSpecs
{
    [Fact]
    public async Task a_proposed_appointment_is_confirmed() { /* … */ }

    // One class, several slices: a method-level binding REPLACES the class's.
    [Fact]
    [BobcatSlice(SliceName = "ProposeHomeCheckAppointment", Pattern = "Automation")]
    public async Task an_accepted_assignment_proposes_a_visit() { /* … */ }
}
```

### Binding, not roles -- the roles come from the vocabulary (bobcat#462) {#event-model-roles}

`[BobcatSlice]` says *which* slice. It never states what the slice's command, events, aggregates, read
models or messages are -- and since bobcat#462 it does not need to, because the test body already says
so. A scenario base whose vocabulary methods carry `[EventModelRole]` hands the generator every role
the slice needs:

```csharp
await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));   // aggregate Account, arranged AccountOpened
await WhenReceived(new Deposit(id, 10m));                        // the act: command Deposit
ThenEvents(typeof(FundsDeposited));                              // emitted FundsDeposited
ThenMessageSent<DepositReceipt>();                               // published DepositReceipt
```

That is `CommandType = Deposit`, `AggregateTypes = [Account]`, `EmittedEvents = [FundsDeposited]`,
`PublishedMessages = [DepositReceipt]`, `Pattern = Command`, and a specification whose `ResolvedTypes`
are those five types in call order. Nothing in the test is authored for the model.

The vocabulary declares its roles, one attribute per role, naming where the type comes from:

```csharp
[EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]       // a type parameter: the type argument
[EventModelRole(SliceRole.ArrangedEvent, nameof(events))]        // a value parameter: each argument's type
public Task GivenEvents<TAggregate>(Guid id, params object[] events) where TAggregate : class

[EventModelRole(SliceRole.Command, nameof(command))]
[EventModelTrigger(TriggerKind.Http, Route = nameof(route))]     // the act's trigger kind and route
public Task WhenPosted(object command, string route)
```

| `SliceRole` | Reaches the slice as |
|---|---|
| `Command` | `Commands`; the **last** one a test sends is the act, its `CommandType` (earlier ones arranged state) |
| `Aggregate` | `AggregateTypes`; nothing on a **Query** slice (#501) |
| `Event` | `EmittedEvents` |
| `ArrangedEvent` | `ConsumedEvents` on a **View** slice, nothing on a Command slice -- there it is the aggregate's own history (#297) -- and nothing on a Query slice, where it is setup (#501) |
| `ReadModel` | `ReadModelTypes` on a View (or pattern-less) slice; `ReadsFrom` on a **Query** slice (#501); nothing on a **Command**, Automation or Translation slice, where a read model the spec asserts is downstream of the act (#681) |
| `Document` | the specification's resolved types only, never a slot |
| `Message` | `PublishedMessages` |
| `Context` | `ReadsFrom` -- a stream the act only looks up, never an aggregate (#502) |
| `ArrangedReadModel` | `ReadModelTypes` on a **View** slice; `ReadsFrom` on any other -- a document stored before a command is a lookup it decides against (#614) |

**A lookup stream is context, not an aggregate (bobcat#502).** `GivenEvents<Client>(…)` says Client is an
aggregate of the slice. When the spec arranges Client only so the handler can load it -- an `OpenAccount`
whose endpoint checks the client exists -- write `GivenContext<Client>(…)` instead. It appends exactly what
`GivenEvents` appends and renders the same step; the difference is the model's. Client lands in the slice's
`ReadsFrom` (jasperfx#824), and upstream draws it as a read model linked into the slice's processor. Its
events are arranged events, as for `GivenEvents`.

```csharp
await GivenContext<Client>(clientId, new ClientEnrolled(clientId, "Alice"));   // ReadsFrom Client
await WhenPosted(new OpenAccount(clientId), "/api/accounts");                  // the act
await ThenAggregate<Account>(TheStartedStream<Account>());                      // aggregate Account
```

<!-- JEREMY: when to reach for GivenContext, in your words -->

**A stored document is a lookup too, unless the slice is a View (bobcat#614).** In a document app the usual
arrange for a command is the documents its endpoint loads with `[Entity]`, stored with `GivenReadModel<T>`.
That method carries `SliceRole.ArrangedReadModel`, which is decided per slice once every spec has folded in,
like `ArrangedEvent`: on a View slice the type is the slice's read model, and on a Command (or Query, or
Automation) slice it goes to `ReadsFrom`, so the canvas does not draw the command as producing those views.

```csharp
await GivenReadModel<Passenger>(passenger);                       // ReadsFrom Passenger
await GivenReadModel<Flight>(flight);                             // ReadsFrom Flight
await WhenPosted(new CreateBooking(passenger.Id, flight.Id), "/api/bookings");   // a Command slice
```

**A read-only act is a Query (bobcat#501).** A vocabulary method with a `GET` or `HEAD` trigger, a `ReadModel`
role and no `Command` role is a *read act*:

<<< ../samples/BankAccountES/Tests/BankAccountSpec.cs#when-reading{cs}

The last act a test makes -- a command or a read -- is its act. When a slice has a read act and no command
act, and declares no `Pattern`, it's a `Query` (JasperFx 2.85.0):

- what it reads -- the read act's type and any asserted read model -- goes to `ReadsFrom`, never
  `ReadModelTypes`, so JasperFx's `ReadModelRead` link joins it to the View slice that produces that read model;
- a stream its specs arranged with `GivenEvents` stamps **nothing**: no `ReadsFrom`, no `AggregateTypes`, no
  `ConsumedEvents`. It's setup feeding a projection, not something the endpoint reads. A stream the endpoint
  does read is a `GivenContext`, which goes to `ReadsFrom` as on any slice;
- it has no `CommandType` and no `AggregateTypes`, even when a test sent a command to arrange state first;
- the act's `GET` route is its trigger. On a slice that comes out anything but a Query -- a command act on the
  same slice wins, whichever folds first -- the read act's trigger is left off.

A declared `Pattern` (#323) still wins. A Query that emits events, starts a stream or sends a message is stamped
as stated, and JasperFx reports it as a `PatternViolation` hotspot.

The trigger is what tells a read act from an assertion: `ThenReadModel<T>` carries the same `ReadModel` role
and no trigger. So no new marker was needed. A `POST` with a read role is not a read act.

<!-- JEREMY: why a GET is its own slice rather than part of the View it reads -->

A value parameter's type is read off the argument: `new T(…)`, `typeof(T)`, `Specify<T>()` at the root of
a `.With(…)` chain, `Expect.Value(…)`, `Tagged(value, …)`, or the argument's static type when it is a
concrete type of the application's own. A `params` parameter contributes every element. An argument of
no knowable type -- `object`, an interface, table rows, `null` -- contributes **nothing**: a missing arrow
is recoverable, a wrong one is believed. So does a type the generated code cannot name: a `private`,
`protected` or `file` record nested in the test class stamps no role.

`From` may also name a type parameter of the class declaring the method. A scenario base
`AggregateScenario<TAggregate>` whose `GivenStream(Guid id)` carries
`[EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]` stamps `Account` in every test deriving from
`AggregateScenario<Account>`.

**The act is a call the test makes.** A role-stamped call inside a local function or a lambda still
contributes its roles, but it is never the act: whether, and when, it runs is not something the generator
can see.

**A wrapper should re-declare the attributes.** The generator reads the attributes on the method a test
calls -- walking an `override` back to its base. A `new` method hiding the vocabulary's, or a helper such as
`GivenAccount(id, events) => GivenEvents<Account>(id, events)`, stamps no role unless it carries its own
`[EventModelRole]` / `[EventModelTrigger]`, naming its own parameters.

**An act behind a helper is still the act (bobcat#681).** One thing is read through a helper of the project's
own (source in the same compilation, followed up to three calls deep): whether it makes an act. A
`WhenPosted(object command, string url)` that wraps `WhenActing` or `WhenTracked` and declares nothing is the
test's act. Before #681 the act vanished with the helper, so `[BobcatSpec(typeof(AddBook))]` over
`WhenPosted(new AddBook(…), "/books")` followed by a `ThenReadModel<…>` made a **View** slice with no command.
Now:

- a command the helper's own body names (`OpenTheAccount() => WhenReceived(new OpenAccount(…))`) is the act's
  command, with the trigger the inner act declares;
- a command passed through a parameter can't be read through the helper. When the call passes an instance of
  the type the spec is bound to (`[BobcatSpec(typeof(AddBook))]` and a `new AddBook(…)` argument), that type is
  the command. Otherwise the act names no command, as below;
- [**BOBCAT045**](diagnostics.md#bobcat045) (info) at the call says so, because the trigger (HTTP verb and
  route) still stops at the helper's parameters. Declare the attributes on the helper and it goes away:

```csharp
[EventModelRole(SliceRole.Command, nameof(command))]
[EventModelTrigger(TriggerKind.Http, Route = nameof(url), HttpMethod = "POST")]
protected Task WhenPosted(object command, string url) => WhenActing(…);
```

**Why an attribute, read from metadata, and not a list of method names.** The vocabulary lives in
another package -- `WolverineScenario` is in WolverineFx.Bobcat, in another repository. A name list in the
generator would let a rename there stop the roles being stamped with no error anywhere. The attribute
travels into metadata with the method, so Wolverine declares its own roles. Write `From`, `Route` and `RouteName` with `nameof` and a rename keeps them in step; a name
that matches nothing is [**BOBCAT034**](diagnostics.md#bobcat034) where the vocabulary is compiled.

The command half also rides JasperFx's `SpecificationBindingDescriptor` (jasperfx#995): the act is its
`CommandType`, and its `ResolvedTypes` are the same list the slice's specification carries.

A test with roles and **no** binding lands on a slice named after its feature. A test with neither
contributes nothing.

### An act that names no command (bobcat#615) {#unstamped-act}

Not every act names its command. `WhenPosted(new { }, route)` and `WhenDeleted(new { product.Id }, route)` pass
an anonymous body, `WhenReceived(someObject)` passes an `object`, and `WhenActing("the sweep runs", …)` runs a
lambda. The test still has an act; the generator just can't tell which command it sends. A call is an act when
its vocabulary method declares a `Command` role (whether or not the argument names a type), an
`[EventModelTrigger]`, or **`[EventModelAct]`** -- the marker for an act with neither, which
`CritterStackSpec.WhenActing` and `WhenActingOn<T>` carry.

When a test's last act names no command:

- **its slice is never inferred a View.** An asserted read model is not what a delete or an accept is about. When
  the act is an HTTP write -- `POST` (the default verb), `PUT`, `PATCH` or `DELETE` -- the slice is a **Command**
  slice with no `CommandType` (bobcat#681 revised #615 here, which left it null): a body-less
  `POST /books/{id}/return` is a command whatever it carries. Any other such act (`WhenActing`, a non-HTTP
  `WhenReceived(someObject)`) leaves the pattern **null** -- a null pattern doesn't miscolour the canvas, a wrong
  one does. A declared `Pattern` still wins;
- **an earlier command is not taken for the act.** A `WhenReceived(new OpenAccount(…))` that arranged state is
  not the slice's `CommandType`, and the spec manifest doesn't infer one either;
- **its trigger is kept.** A body-less `DELETE` to `/products/{id}` is still an HTTP slice with that route.

The fix is to name the command: send a typed command, or bind the spec with `[BobcatSpec(typeof(TheCommand))]`
and pass an instance of it to the act (above).

**Receiving an event is an Automation (bobcat#681).** `WhenReceived(new HomeCheckAssignmentAccepted(…))` names
the type it sends as a command, but when any specification in the compilation emits or arranges that type
(`ThenEvents`, `ThenEmitted`, `GivenEvents`, …), it is an event, and a slice whose act receives it is the system
reacting: its pattern is `Automation`, with the event as its `CommandType` -- the shape Wolverine derives for a
handler of another slice's event, and what JasperFx's `EventTriggers` link joins on. An event no specification
names can't be told from a command, so that slice stays `Command`; declare `Pattern = "Automation"` for it.

**Unbound specs with different acts are not folded into one slice.** The feature-named slice is a fallback for a
feature whose unbound specs all make the same act, or none. When they make different ones, a spec whose act is a
command lands on the slice named for that command -- where `[BobcatSpec(typeof(TheCommand))]` would put it -- and
a spec whose act is a query or names no command is left off the model, with [**BOBCAT041**](diagnostics.md#bobcat041) saying so. Before, a
`ReturnBook` spec and an on-loan list query both landed on a slice named after their feature, which came out a
View that existed nowhere in the code.

**A spec that reaches no slice says so.** In a suite whose specs model at least one slice, a `[BobcatSpec]` /
`[BobcatTheory]` with no binding and no role-stamped vocabulary, or whose only stated types are documents (no slice
carries a document), gets **BOBCAT041** (info) at the test. A slice holding only documents is no longer invented.

### Who triggers the slice -- `TriggeredBy` (bobcat#463)

The trigger's *label* is the one Event Modeling fact a test body cannot state, so `[BobcatSlice]` and
`[BobcatSpec]` carry it:

```csharp
[BobcatSlice(SliceType = typeof(Deposit), TriggeredBy = "the account holder")]
```

A method's `TriggeredBy` wins over its class's whichever order the tests fold in -- the #258 rule. So put
it on the class only when every slice the class binds shares the trigger. A method attribute that sets
*only* `TriggeredBy` keeps the class's slice; one that rebinds the slice replaces the class's binding
whole, trigger included.

The trigger's *kind* is not declared here. It is derived from the act -- `[EventModelTrigger]` on the
vocabulary method -- because upstream's `TriggerKind` is a derived fact, never an overlay declaration, and
the act is what knows how it dispatches. Only the act's own trigger counts: an HTTP call that merely
arranges state does not make a message-driven slice an HTTP one. When several specifications of one slice
are HTTP acts, a constant route beats a computed one whatever order they are read in.

### The act's route -- a template, an interpolation or a route name (bobcat#500) {#act-route}

<!-- JEREMY -->

An HTTP act's route reaches the slice as `TriggerOrigin.HttpRoute`. The generator works it out from the
argument the test passes:

| The argument | The slice's route |
|---|---|
| A constant: `"/api/accounts/{accountId}/deposits"`, values after it | Stamped as written |
| An interpolated string: `$"/api/accounts/{theAccount}/deposits"` (a `+` concatenation reads the same) | The template of the **one** endpoint whose literal segments match, with a parameter at every hole: `/api/accounts/{accountId}/deposits` |
| An interpolated string no single endpoint matches | Rebuilt from the interpolation: `/api/accounts/{theAccount}/deposits` |
| A route name, through `RouteName` | The template of the endpoint that declares that name |
| No route at all: a by-type act such as `WhenPosted(cmd)` or `WhenPut(cmd)` (bobcat#613) | The template and verb of the **one** endpoint whose request body is the command's type |
| Anything else: a helper call, a variable | Unknown, and [**BOBCAT038**](diagnostics.md#bobcat038) (info) says so |

The endpoints are the attributes the compilation and its references declare. They're matched by attribute
shape, so Bobcat takes no dependency on either package:

- Wolverine: `[WolverinePost("/api/accounts/{accountId}/deposits", RouteName = "MakeDeposit")]` and the rest of
  the family. A class-level `[RoutePrefix]` is applied the way Wolverine applies it.
- MVC: `[HttpPost("{accountId}/deposits", Name = "MakeDeposit")]` and the rest of the family, and
  `[Route("…", Name = "…")]`. These are combined with the controller's `[Route]` and its `[controller]` /
  `[action]` tokens. A template starting with `/` stands alone.

A minimal API's `.WithName("…")` is a call, not an attribute, so it can only be resolved once the host runs.
Wolverine's namespace and global route prefixes are configuration, so they're the same. A route name declared
only that way leaves the route unknown, and BOBCAT038 says so.

**Matching a by-type act.** An act whose vocabulary takes no `Route` or `RouteName` fills the route from the
command at run time, so the generator looks for the endpoint that takes that command. A parameter of the
command's exact type counts as the body unless it's marked `[FromServices]`, `[FromKeyedServices]`, `[FromRoute]`,
`[FromQuery]`, `[FromHeader]`, `[FromForm]` or `[NotBody]`. When the act declares a verb (`WhenPut`), only an
endpoint of that verb is a candidate. When it declares none, the act takes the endpoint's verb. No endpoint, or
several with different templates, leaves the route unknown, and BOBCAT038 names the endpoints it found. A
minimal API's `MapPost(…)` is a call, so it's never found; pass the route to such an act instead.

**Matching an interpolation.** A hole matches any route parameter (`{id}`, `{id:guid}`, `{id?}`, `{*rest}`).
Literal text compares case-insensitively, and a query string is dropped. When the act declares a verb, an
endpoint with a different verb is not a candidate. If an act declares no verb and one endpoint matches, the
act takes that endpoint's verb. When two templates fit, neither is chosen.

**A rebuilt route names each hole after its expression.** A simple identifier gives its own name, a member
access gives its last member (`theAccount.Id` → `{Id}`), and anything else gives `{value}`. A repeated name is
numbered (`{value2}`). Those names are the test's, not the endpoint's. So a rebuilt route ranks below a
constant or resolved route on the same slice, whatever order the specifications fold in, and above no route at
all.

A vocabulary method addresses an endpoint by name with `RouteName`. It's checked by **BOBCAT034** the same way
`Route` is:

<<< ../samples/BankAccountES/Tests/BankAccountSpec.cs#when-put-to{cs}

[`samples/BankAccountES`](https://github.com/JasperFx/bobcat/blob/main/samples/BankAccountES) uses it: `update_a_client` calls `WhenPutTo(…, "UpdateClient", clientId)` against
`[WolverinePut("/api/clients/{clientId}", RouteName = "UpdateClient")]`. The slice gets
`PUT /api/clients/{clientId}`, the same route Wolverine derives from the chain, so the two sources agree.

The route name rides on the descriptor as `TriggerOrigin.HttpRouteName` (JasperFx 2.85.0), next to the route
it resolved to. A route name the generator can't resolve is stamped too, on an origin with no route, labelled
`PUT (route name UpdateClient)` so it can't be read as a route. JasperFx merges the name on its own: a known
name fills in whichever origin wins, and two different names are a `SourceDisagreement`.

### Two spellings, and why `SliceType` is preferred

`SliceType` means **exactly** `SliceName = type.Name`. No suffix stripping, nothing inferred -- which
is what makes it safe, because passing `typeof(ConfirmAppointmentHandler)` plainly would not produce
the slice name rather than being quietly "corrected".

Prefer it for two reasons. It is rename-safe, and -- the one that matters more -- **a type survives to
the generator where a string does not**, so only `SliceType` can later be cross-checked against what
the model declares.

Reach for `SliceName` where no type bears the slice's name. That is not an edge case: measured across
CritterCrush's nineteen slices, Command slices matched a type 13/13 and View slices 3/3, while
**Automation slices matched 0/3** -- an automation's only type is `{Slice}Handler`, and it has no
command type at all, by design.

### Two diagnostics, so the preference is enforced rather than remembered

| | |
|---|---|
| [**BOBCAT023**](diagnostics.md#bobcat023) (error) | Both spellings set, naming *different* slices. One of them is wrong and no silent winner is the right answer. Setting both to the *same* slice is redundant, not wrong, and is allowed |
| [**BOBCAT024**](diagnostics.md#bobcat024) (warning) | `SliceName` is a literal string and a type of that name exists in this compilation -- use `SliceType`. Silent where no such type exists, which is every Automation |

"`SliceType.Name` matches no slice in the model" is not a generator diagnostic: the generator has no
model to check against. It belongs to the model merge, where a binding naming nothing shows up as an
unmatched slice.

### A method-level binding replaces, it does not override

A rebinding is a *whole* binding. An earlier cut merged the method's tags over the class's, and a
method rebinding to another slice dragged the class's `Domain` / `Chapter` / `Pattern` with it  -- 
stamping them on a slice that already had its own answers. So a test that wants the class's domain as
well restates it.

### One attribute instead of three -- `[BobcatSpec]` (issue #403) {#bobcatspec}

An xUnit spec bound to a slice used to carry three attributes: the `[Fact]`, the `[BobcatSlice]`
that binds it, and the `[BobcatScenario]` that opens the recording. `[BobcatSpec]` is all three:

```csharp
// Before
[Fact, BobcatScenario, BobcatSlice(SliceType = typeof(ConfirmAppointment))]

// After
[BobcatSpec(typeof(ConfirmAppointment))]
public async Task a_proposed_appointment_is_confirmed() { /* … */ }
```

`typeof(X)` positionally means exactly `SliceName = "X"`, the same rule `SliceType` follows.
`SliceName`, `SliceType`, `Domain`, `Chapter` and `Pattern` are all settable by name as well, with
BOBCAT023 and BOBCAT024 unchanged -- including **across the two attributes**, because they are the
same settings under two spellings and a method carrying both is one binding stated twice.

**It closes the BOBCAT028 trap by construction.** That diagnostic exists because a class can bind a
slice and open no recording -- green tests, an unbound slice, and nothing at run time able to tell you
why. The attribute that *claims* the slice is now the attribute that *opens* the recording, so the
two cannot come apart.

**xUnit-only.** It subclasses `Xunit.FactAttribute`. On TUnit, NUnit and MSTest, bind with
`[BobcatSlice]` beside `[BobcatScenario]`.

**Subclassing it** (a `[PostgresFact]`-style attribute of your own) means re-declaring and forwarding
its `[CallerFilePath]`/`[CallerLineNumber]` parameters. `FactAttribute`'s only constructor takes that
pair and the compiler fills them at the call site it sees; a subclass that calls `base()` without
re-declaring them hands over *its own* file and line, so every test reports one source location and
IDE test navigation lands on the attribute instead of the test.

### A theory in one attribute -- `[BobcatTheory]` (bobcat#569) {#bobcattheory}

`[BobcatTheory]` is `[BobcatSpec]` for a `[Theory]`: the theory, the scenario bracket and the slice
binding, with the same `Name`, slice settings and `Pending`. **Each row is its own specification**,
named by its arguments or its label:

```csharp
[BobcatTheory(typeof(ApplyDiscount), Name = "Discount is applied")]
[InlineData(100, Tier.Gold)]                          // Discounts/Discount is applied (amount: 100, tier: Gold)
[BobcatExample("silver customer", 50, Tier.Silver)]   // Discounts/Discount is applied [silver customer]
public void discount(int amount, Tier tier) { /* … */ }
```

The Event Model keeps one specification per method, and each row's result names it as its parent.
What the row identity is made of, which rows reach the manifest, and why one row can't be run on its
own by a filter: [spec identities](spec-identities.md#each-data-row-is-its-own-specification).

### A theory's examples as a table -- `[BobcatExamples]` (bobcat#570) {#bobcatexamples}

For a data-centric spec, write the rows as the same pipe-table literal Bobcat reads everywhere else.
**One row, one specification**, each with its own run, verdict and identity:

```csharp
[BobcatTheory(typeof(ApplyDiscount))]
[BobcatExamples("""
    | amount | tier | expected |
    | 100    | Gold | 90       |
    | 100    | None | 100      |
    """)]
public async Task discount_is_applied(decimal amount, Tier tier, decimal expected) { /* … */ }
// Discounts/discount is applied (amount: 100, tier: Gold, expected: 90)
// Discounts/discount is applied (amount: 100, tier: None, expected: 100)
```

- **Columns bind to parameters by name**, case-insensitive: a `[Header("…")]` title on the parameter,
  else its name -- the rule `TableRunner` binds a row method by. A parameter with a default value may
  be left out.
- **Cells read through `CellValues`**: `NULL`, `EMPTY`, a quoted literal, relative times
  (`TODAY+2`), enums by name (any case), and domain types (`CellValues.Register<T>`, `IParsable<T>`,
  a `TypeConverter`, a `ctor(string)`). Rows are read when xUnit **discovers** the theory, so register
  a domain type's conversion in a `[ModuleInitializer]`, not in a fixture, and a relative time
  resolves against the real clock, not one the spec fakes.
- **The table is a constant, so the build checks it.** A column naming no parameter is [**BOBCAT032**](diagnostics.md#bobcat032).
  A parameter with no column and no default, a column given twice, a row with the wrong number of
  cells, a header with no rows, or a cell its parameter's type can't read (`ten` for an `int`, an enum
  value no member names, `NULL` for a non-nullable value type) is [**BOBCAT042**](diagnostics.md#bobcat042). A constant built from
  `nameof` (`$"| {nameof(amount)} |…"`) is read too.
- **Each row is listed in the spec manifest** under its identity when that identity is certain at
  compile time: strings and the tokens, enums, numbers, booleans, chars and Guids. A row with a date,
  a relative time or a domain type still runs under its own identity; it just isn't listed ahead of
  the run. Two rows that read alike (`1` and `01`) are [BOBCAT036](diagnostics.md#bobcat036).
- **Not `TableRunner`.** That judges many rows inside **one** spec, with one verdict. Reach for
  `[BobcatExamples]` when each row is an example someone may want to run, track or see fail on its
  own; reach for a table step when the rows are one claim.
- `[AttributeUsage(AllowMultiple = true)]`, so a theory can carry several tables, and mix them with
  `[InlineData]` and `[BobcatExample]`. xUnit-only, like `[BobcatTheory]`.

### An explicit scenario name: `Name = "…"` (bobcat#489) {#explicit-name}

A projected spec's scenario title is its method name read as a sentence. Two things a method name
can't do: tell apart two specs whose names read the same under one feature, and spell an apostrophe,
a dash or a comma. `Name` states the title outright:

```csharp
[BobcatSpec(typeof(ConfirmAppointment), Name = "A volunteer can't re-confirm, twice")]
public async Task volunteer_cannot_reconfirm() { /* … */ }
```

The identity is now `Booking/A volunteer can't re-confirm, twice`, verbatim, the same way a
`[BobcatFeature("…")]` title is. It reaches everything the identity does -- the declared steps, the
recorder's `scenario_finished`, the spec manifest, running by identity, and the Event Model's spec
list -- because the generator and the runtime read it through the same derivation. A `const` or a `nameof` works as well as a
literal.

Leave it off and nothing changes: an identity that states no name is byte-for-byte the string it was
before. A null, empty or all-whitespace name counts as no name. Setting one *does* change the
identity, which is the point, so it also moves that spec's ledger history to the new string.

It is not xUnit's `DisplayName`, which renames the test in a runner's output and nothing else;
Bobcat doesn't read that. And like `Pending`, it is on `[BobcatSpec]` and `[BobcatTheory]`, so it is
xUnit-only.
On the other runners the fix for a collision is a distinct method name.

### Stub-first: `Pending = true` (issue #404)

Stub-first work produces specifications that exist before their behaviour does. Without a marker for
it, a scaffolded skeleton **threw**, so a pending spec was indistinguishable from a failing one -- in
the test report and on the canvas both. Upstream already has the right shape for one: a
`PendingSpecification` hotspot (jasperfx#689), which `SpecIdentityAudit` treats as *joined*, not drift.

```csharp
[BobcatSpec(typeof(ConfirmAppointment), Pending = true)]
public async Task a_confirmed_appointment_cannot_be_confirmed_twice()
    => throw new NotImplementedException();   // never runs
```

Three things follow, and they are the issue's acceptance:

| | |
|---|---|
| **A hotspot, not a specification** | The identity lands on the slice as `HotspotDescriptor.PendingSpecification`, so the slice does not look *verified* by a stub -- while still being **joined**, so the audit reports neither an orphan nor a hole |
| **Skipped, never swallowed** | It sets `FactAttribute.Skip`, so every runner and IDE reports it as a skip: nobody ran it. Running the body and absorbing the failure would launder red into green, which is the one thing a pending marker must not do. An explicit `Skip` of your own wins, in either written order |
| **It disappears on its own** | Delete `Pending = true` and it is an ordinary specification again. There is no second place to update, which is what keeps a stub from outliving its stub phase |

**"No steps" does NOT mean pending**, deliberately. A test with no marker comments says plenty -- it
runs real code and publishes a real verdict -- and **most of this repository's own projected samples
declare no marker steps at all**. Inferring pending from an empty step list would
have turned most of a working suite into open questions on the canvas.

**It is on `[BobcatSpec]` and `[BobcatTheory]`, not on `[BobcatSlice]`.** Putting it there would let
a TUnit suite write it, but that attribute is not the test: it would produce the hotspot while the
body still ran and still failed -- precisely the "indistinguishable from a failing one" state this
exists to end. So `Pending` is xUnit-only; on the other runners, use the runner's own skip.

A pending spec **stays in the spec manifest and in `--list-tests`**, because xUnit discovers a
skipped test like any other. That is the right answer rather than an accident: a resident runner can
be asked to run it, and gets a skip.

**Strict runs fail it.** Under `BOBCAT_LIFECYCLE=strict` a pending spec is not skipped: it
fails without running, as `pending specification in strict mode: <identity>`. See the next section.

### Not yet passing: `Lifecycle = Lifecycle.Acceptance` (bobcat#545) {#acceptance}

`Pending` is for a stub that cannot run at all. Spec-first work has a second state: a specification
that *does* run, against behaviour that is not finished yet. Skipping it hides the progress; leaving it
an ordinary spec turns CI red. An Acceptance spec is the answer:

```csharp
[BobcatSpec(typeof(ConfirmAppointment), Lifecycle = Lifecycle.Acceptance)]
public async Task a_confirmed_appointment_cannot_be_confirmed_twice() { /* a real spec */ }
```

On TUnit, NUnit and MSTest it is a property of `[BobcatScenario]`, on the class (every test in it) or
on a method (that test, beating its class's): `[BobcatScenario(Lifecycle = Lifecycle.Acceptance)]`.

| | |
|---|---|
| **It runs every time, and is never retried** | Nothing about it is skipped up front |
| **A failure is recorded in full under its own verdict** | Every step and cell, on the console (`ACCEPTANCE: FAILING`), in the JSON report (`"verdict": "acceptance: failing"`), and on the monitor wire (`scenario_finished.outcome` = `AcceptanceFailing`, never `Failed`). Nothing is swallowed, which is what separates it from the run-and-absorb #404 rejected |
| **The test platform reports it SKIPPED** | With the reason `acceptance spec, not yet passing: <first failure>`, so the build stays green |
| **A passing one says it is ready to promote** | `OK (acceptance — ready to promote to Regression)`: delete `Lifecycle = Lifecycle.Acceptance` |
| **`BOBCAT_LIFECYCLE=strict` makes its failures real** | For a branch claiming done. Strict also fails every `Pending = true` spec: strict means nothing unfinished |

How each runner reports the skip:

| Runner | Mechanism | Limit |
|---|---|---|
| xUnit v3 | `[BobcatSpec]`'s own test case rewrites the test's `TestFailed` into a `TestSkipped` (a skip thrown from the after-test hook is not enough when the body threw: xUnit aggregates the two exceptions and never reads an aggregate as a skip) | `[BobcatSpec]` only, not `[Fact]` + `[BobcatScenario]` |
| TUnit | `TestContext.Execution.OverrideResult(TestState.Skipped, reason)` | none |
| NUnit | the result is replaced with `ResultState.Ignored`, what `Assert.Ignore` reports | none |
| MSTest | `AssertInconclusiveException` from the global cleanup hook | **only a spec failed by gathered `SpecAssert` wrongs.** MSTest keeps the more important outcome, so a body that *threw* stays failed -- and Bobcat records that one as `Failed`, not `acceptance: failing`, so no surface claims the build stayed green |

**Known limit:** in an IDE or a TRX file a failing Acceptance spec looks like any other skip; only the
reason text tells it apart. Bobcat's own surfaces are where the distinct verdict lives.

In the [**results file**](bobcat-tool.md#results-file) (`BOBCAT_RESULTS`, `bobcat run --results`) such a spec is listed in full with
`"verdict": "acceptance: failing"` and counted in `summary.acceptanceFailing`, never in `failed` or
against `succeeded`. `bobcat run --failed` does not re-run it, since the run it came from was green.

**A lost environment is never softened** ([environment lost](#environment-lost)). An Acceptance spec
that throws `SpecCatastrophicException` is a plain failure, because it is the cause every halted
scenario names; and one halted after the loss fails before its body runs, where no runner can report
a skip, so Bobcat reports it failed too.

## The honest limits

- **A comment-declared step still has no clock of its own.** It reports the work observed *inside*
  it (issue #304) -- the decorated helpers that ran under it, their verdicts, and the sum of their
  durations -- and a region containing none of them says nothing at all. Timing needs somewhere to
  intercept and a comment does not give one; what changed is that the work underneath it is now
  attributed to it, exactly, from the call site's line.
- **No lifecycle timing.** Fixture setup belongs to the test runner and runs outside the scenario
  bracket, so the timing summary reports it as not measured rather than as zero. See
  [Where the time went](#timing).
- **A step outside a scenario is silent.** Decorated helpers get called from plenty of places that
  are not specifications, and reporting from them would be noise.
- **An exception ends the test, so the steps after it are never judged.** This is the one
  irreducible difference between a spec engine and a test method. It is a design decision rather
  than a bug -- a continue-past-an-exception step bracket would have to
  swallow and record, which changes what a test body means. `SpecAssert` is the way around it for a
  comparison you *expect* might disagree.
- **A marker comment cannot carry a cell.** A comment *declares* a step; it does not execute one, so
  there is no step object for a comparison to attach to. A narrated test's finest available verdict
  is its own exception -- one failure, with no expected/actual pair. A decorated helper reporting
  through `SpecAssert.Check` is what gets the cell.
- **Your application's own `ILogger` output is not captured.** A step can say what the system did
  with [`SpecLog.Write`](#step-logs); capturing the app's
  logs is post-1.0.
- **Test methods are matched by attribute name** -- `Fact`, `Theory`, `Test`, `TestCase`,
  `TestMethod`, `DataTestMethod`, `BobcatSpec`, `BobcatTheory` -- so the
  generator needs no reference to a runner it is trying to stay neutral about.

## Diagnostics

The generator reports what it can see at build time: a template placeholder naming no parameter
(BOBCAT027), a slice bound with nothing recording it (BOBCAT028), a comment read as prose (BOBCAT029),
a stale table column (BOBCAT032), duplicate identities (BOBCAT036), interceptors not enabled
(BOBCAT037), an unreachable step helper (BOBCAT039), an inline examples table that doesn't fit its
theory (BOBCAT042), a Task-returning step that is never awaited (BOBCAT043), a member check that is not one assertion
on one member (BOBCAT044), and the Event Model ones (BOBCAT023, 024, 034, 038, 040, 041). Each has its
symptom, cause and fix on the [Diagnostics](diagnostics.md) page, along with the ids retired with the
Gherkin lane.

## The sample corpus

[`src/Bobcat.Xunit.Samples`](https://github.com/JasperFx/bobcat/blob/main/src/Bobcat.Xunit.Samples)
is a corpus of ordinary xUnit v3 specifications -- sentences, facts, output parameters, tables, sets,
decision tables, narrated tests, the projected-assertion style, Acceptance and pending specs. **Many
of them fail on purpose**, because the samples exist to show what each outcome *looks* like.

```bash
dotnet build src/Bobcat.Xunit.Samples/Bobcat.Xunit.Samples.csproj
./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples
```

For the Critter Stack vocabulary on a real application,
[`samples/BankAccountES`](https://github.com/JasperFx/bobcat/blob/main/samples/BankAccountES) runs its
specifications on `CritterStackSpec` against Marten and Fisher, with its own HTTP and message acts on
top. `src/Bobcat.Projected.Acceptance.Tests` and `src/Bobcat.CritterStackScenario.Tests` pin the
behaviour this page describes.
