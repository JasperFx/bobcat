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
time but no scenario is ever opened, so nothing publishes, and **BOBCAT028** says so at build time.

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
line that a sibling project also needed, where forgetting it is a `CS9137` build failure rather than
a missing feature.

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

An expression is a **named template** -- Storyteller's `[FormatAs]` syntax. Each `{placeholder}`
names a **parameter** of the method and is filled from that argument at the call site: `"Adding {x}
to {y} should equal {sum}"` over `double Adding(double x, double y, double sum)`.

**Placeholders bind by name only.** Cucumber type captures (`{int}`, `{string}`, `{word}`, optional
text, raw regex) are not supported: they only ever worked by matching Gherkin text against the
expression, and that lane is gone (#471). `[Then("the message ends {word} on attempt {int}")]` over
`(string outcome, int attempt)` fills nothing -- it is a `BOBCAT027` warning and renders literally.
Write `[Then("the message ends {outcome} on attempt {attempt}")]` instead. A name such as
`{aggregate}` or `{event}` works when a parameter has that name (`Type aggregate`, `Type @event`);
a `System.Type` argument renders as the type's name.

**Keywordless is a real choice, not a default.** Storyteller sentences and Gauge steps read as prose,
and "Multiply by 3 then add 4" is not a Given, a When or a Then:

```csharp
[Step("Multiply by {multiplier} then add {delta}")]
internal void MultiplyThenAdd(int multiplier, int delta) { … }
```

An empty keyword renders no label, and it is never promoted to `And`, because `And` is a word the
author never wrote.

### A decorated helper cannot be `protected`

A C# interceptor must be an extension method, and an extension method cannot see a `protected`
member. So every decorated helper has to be `internal` or `public`. This is a language rule rather
than a Bobcat design choice, and it is the one real cost of step helpers -- widening eight helpers on
`DaemonContext` is exactly what Marten's PR did. Marker comments have no such constraint.

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

**A placeholder that names no parameter is a warning** -- `BOBCAT027`, once per helper -- so a
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

The two compose. Comments give a test its narrative; decorated helpers give real per-step timing
across every test in the suite that touches them.

### A step need not be a keyword -- `// *` (issue #324)

Bobcat's rendering does not depend on Given / When / Then, and should not. The inspiration is
ThoughtWorks' **Gauge**, whose specifications are bulleted sentences with no keyword vocabulary at
all, and **Storyteller**, whose specs read as prose rather than as a keyword table:

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

The variable is deliberately a **tri-state**. Collapsed to a bool, "somebody said no" and "nobody
said" are the same value, and `BOBCAT_SPEC_CONSOLE=0` would have printed anyway.

`BOBCAT_SPEC_PREVIEW=1` is the other half -- every specification **without** its results, including
which helper each step binds to. Pair it with the platform's own `--list-tests` to preview without
executing anything at all: the plan is registered by a module initializer, so it is known before a
single test runs.

```bash
BOBCAT_SPEC_PREVIEW=1 ./MySpecs --list-tests
```

Both are written at **process exit**, not after the last test -- nothing here knows which test is the
last one, and the runner does not say. `ProjectedSpecConsole.Captured` is the seam for a consumer that
would rather render or assert on the specifications itself, and `JsonRenderer.RenderScenario` turns
one into the agent-facing JSON.

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
show its *first* disagreement, with every later step unrun. Storyteller's `Asserting_Values.md`
sample is the case that makes it concrete: it reaches five sentences and shows five verdicts, and its
own comment says the middle one was written wrong on purpose.

`SpecAssert` is how a step reports a comparison without ending the test:

```csharp
[Then("The value should be {value}")]
internal void TheValueShouldBe(double value) => SpecAssert.Check("value", _calculator.Value, value);
```

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

A `bool`-returning step is the other shape, unchanged from Storyteller: the answer *is* the verdict,
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

### Saying what the system did -- `SpecLog.Write` (bobcat#547)

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
  scenario. A passing one shows none unless the run sets `BOBCAT_VERBOSE_REPORTS=1`. So a green
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
- **Where they go.** With a results directory (`BOBCAT_RESULTS`, `bobcat run --results`) each is a
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
| `TableRunner.BuildRows<T>(table)` | one `T` per row -- Storyteller's `CreateNewObject<T>` |
| `TableRunner.Run(this, nameof(rowMethod), table)` | a private method once per row, with the before/after envelope written in the step's own body. A returned value plus one unclaimed column is a decision table |
| `SpecAssert.Check(column, actual, expected, rowIndex: i)` | a decision table reported row by row |
| `SetVerificationComparer.Verify(actual, expected, keyColumns: "Name")` | a set verification, matched by key, with `MISSING`, `EXTRA`, `FAIL` and `ORDER` rows |
| `PropertyCells.Verify(subject, expected)` | one object against one row, on only the columns the row names. `Verify(subject, table)` on a `CritterStackSpec` is the same check |

## Projecting the assertions you already wrote (opt-in)

With `<BobcatProjectAssertions>true</BobcatProjectAssertions>` in the project, an ordinary
statement-level Shouldly call inside a `[BobcatFeature]` test renders as a step -- no attribute, no
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
for the whole rule, and [The Resident Runner](resident-runner.md) for the loop a console drives it
from.

Two details matter if you are reading the manifest yourself. The binding is **recorded, not
re-derived** -- the identity is a one-way function of the names, and an explicit
`[BobcatFeature("…")]` title shares nothing with its class name. And it is emitted on a wider rule
than declared steps: what makes a test a specification is that a scenario is recorded around it, not
that it declared any steps. Keyed on declared steps, the first cut of this listed **8 of
`Bobcat.Xunit.Samples`' 41 specifications** while the other 33 rendered and published verdicts
perfectly well.

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
| `ReadModel` | `ReadModelTypes`; `ReadsFrom` on a **Query** slice (#501) |
| `Document` | the specification's resolved types only, never a slot |
| `Message` | `PublishedMessages` |
| `Context` | `ReadsFrom` -- a stream the act only looks up, never an aggregate (#502) |

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

**A read-only act is a Query (bobcat#501).** A vocabulary method with a `GET` or `HEAD` trigger, a `ReadModel`
role and no `Command` role is a *read act*:

```csharp
[EventModelRole(SliceRole.ReadModel, nameof(T))]
[EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "GET")]
public Task WhenReading<T>(string route, params object[] routeValues) where T : class
```

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

**A wrapper must re-declare the attributes.** The generator reads the attributes on the method a test
calls -- walking an `override` back to its base -- and never what that method's body calls. A `new` method
hiding the vocabulary's, or a helper such as `GivenAccount(id, events) => GivenEvents<Account>(id, events)`,
stamps nothing unless it carries its own `[EventModelRole]` / `[EventModelTrigger]`, naming its own
parameters.

**Why an attribute, read from metadata, and not a list of method names.** The vocabulary lives in
another package -- `WolverineScenario` is in WolverineFx.Bobcat, in another repository. A name list in the
generator would let a rename there stop the roles being stamped with no error anywhere. The attribute
travels into metadata with the method, so Wolverine declares its own roles. Write `From`, `Route` and `RouteName` with `nameof` and a rename keeps them in step; a name
that matches nothing is **BOBCAT034** where the vocabulary is compiled.

The command half also rides JasperFx's `SpecificationBindingDescriptor` (jasperfx#995): the act is its
`CommandType`, and its `ResolvedTypes` are the same list the slice's specification carries.

A test with roles and **no** binding lands on a slice named after its feature. A test with neither
contributes nothing.

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
| Anything else: a helper call, a variable | Unknown, and **BOBCAT038** (info) says so |

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

```csharp
[EventModelRole(SliceRole.Command, nameof(command))]
[EventModelTrigger(TriggerKind.Http, RouteName = nameof(routeName), HttpMethod = "PUT")]
public Task WhenPutTo(object command, string routeName, params object[] routeValues)
```

`samples/BankAccountES` uses it: `update_a_client` calls `WhenPutTo(…, "UpdateClient", clientId)` against
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

### Three diagnostics, so the preference is enforced rather than remembered

| | |
|---|---|
| **BOBCAT023** (error) | Both spellings set, naming *different* slices. One of them is wrong and no silent winner is the right answer. Setting both to the *same* slice is redundant, not wrong, and is allowed |
| **BOBCAT024** (warning) | `SliceName` is a literal string and a type of that name exists in this compilation -- use `SliceType`. Silent where no such type exists, which is every Automation |
| *(not a generator diagnostic)* | "`SliceType.Name` matches no slice in the model" cannot live here: the generator has no curated model to check against. It belongs to the model merge, where a binding naming nothing shows up as an unmatched slice |

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

**xUnit-only, for now.** It subclasses `Xunit.FactAttribute`, so TUnit needs its own equivalent;
that is a follow-up. On TUnit, NUnit and MSTest, bind with `[BobcatSlice]` beside `[BobcatScenario]`.

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
list -- because the generator and the runtime read it through the same derivation, and
`MarkerSpecNamingAgreementTests` keeps the two together. A `const` or a `nameof` works as well as a
literal.

Leave it off and nothing changes: an identity that states no name is byte-for-byte the string it was
before. A null, empty or all-whitespace name counts as no name. Setting one *does* change the
identity, which is the point, so it also moves that spec's ledger history and its curated-model
scenario to the new string.

It is not xUnit's `DisplayName`, which renames the test in a runner's output and nothing else;
Bobcat doesn't read that. And like `Pending`, it is on `[BobcatSpec]`, so it is xUnit-only for now.
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
runs real code and publishes a real verdict -- and **33 of this repository's own 41 projected samples
declare no marker steps at all**. Inferring pending from an empty step list would
have turned most of a working suite into open questions on the canvas.

**It is on `[BobcatSpec]` and not on `[BobcatSlice]`.** Putting it there would let a TUnit suite
write it, but that attribute is not the test: it would produce the hotspot while the body still ran
and still failed -- precisely the "indistinguishable from a failing one" state this exists to end. A
TUnit suite gets it with TUnit's own equivalent of `[BobcatSpec]`.

A pending spec **stays in the spec manifest and in `--list-tests`**, because xUnit discovers a
skipped test like any other. That is the right answer rather than an accident: a resident runner can
be asked to run it, and gets a skip.

**Strict runs fail it (C13).** Under `BOBCAT_LIFECYCLE=strict` a pending spec is not skipped: it
fails without running, as `pending specification in strict mode: <identity>`. See the next section.

### Not yet passing: `Lifecycle = Lifecycle.Acceptance` (bobcat#545)

`Pending` is for a stub that cannot run at all. Spec-first work has a second state: a specification
that *does* run, against behaviour that is not finished yet. Skipping it hides the progress; leaving it
an ordinary spec turns CI red. Storyteller's answer was the Acceptance/Regression split, and this is it:

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

In the **results file** (`BOBCAT_RESULTS`, `bobcat run --results`) such a spec is listed in full with
`"verdict": "acceptance: failing"` and counted in `summary.acceptanceFailing`, never in `failed` or
against `succeeded`. `bobcat run --failed` does not re-run it, since the run it came from was green.

**A lost environment is never softened** ([environment lost](#environment-lost)). An Acceptance spec
that throws `SpecCatastrophicException` is a plain failure, because it is the cause every halted
scenario names; and one halted after the loss fails before its body runs, where no runner can report
a skip, so Bobcat reports it failed too.

Two implementation facts worth knowing, both pinned by tests rather than left as comments:

- **It is both a Fact and a test-bracket hook because xUnit v3 collects hooks by the
  `IBeforeAfterTestAttribute` *interface***, not only from the `BeforeAfterTestAttribute` base
  class. Those two are siblings -- both derive straight from `Attribute` -- so inheriting from both
  was never possible.
- **It re-declares and forwards `[CallerFilePath]`/`[CallerLineNumber]`, and a subclass of it must
  too.** `FactAttribute`'s only constructor takes that pair and the compiler fills them at the call
  site it sees; a subclass that calls `base()` without re-declaring them hands over *its own* file
  and line, so every test in the suite reports one source location and IDE test navigation lands on
  the attribute instead of the test.

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
  irreducible difference between a spec engine and a test method: Storyteller's
  `Facts_in_Action.md` reaches all five of its lines and the projected version reaches four. It is a
  design question rather than a bug -- a continue-past-an-exception step bracket would have to
  swallow and record, which changes what a test body means. `SpecAssert` is the way around it for a
  comparison you *expect* might disagree.
- **A marker comment cannot carry a cell.** A comment *declares* a step; it does not execute one, so
  there is no step object for a comparison to attach to. A narrated test's finest available verdict
  is its own exception -- one failure, with no expected/actual pair. A decorated helper reporting
  through `SpecAssert.Check` is what gets the cell.
- **A recorded step has no logs and no diagnostics**, so Storyteller's `Context.Reporting.Log(...)`
  has no equivalent yet.
- **Test methods are matched by attribute name** -- `Fact`, `Theory`, `Test`, `TestCase` -- so the
  generator needs no reference to a runner it is trying to stay neutral about.

## Diagnostics

| | |
|---|---|
| **BOBCAT023** (error) | `[BobcatSlice]` or `[BobcatSpec]` sets both `SliceName` and `SliceType`, naming different slices. See [above](#three-diagnostics-so-the-preference-is-enforced-rather-than-remembered) |
| **BOBCAT024** (warning) | `SliceName` is a literal string and a type of that name exists in this compilation -- use `SliceType` |
| **BOBCAT027** (warning) | A step template names no parameter of its method. Nothing can fill it, so the step renders as `{thread}` forever -- before this it looked identical to a placeholder that was merely deferred |
| **BOBCAT028** (warning) | A `[BobcatFeature]` class that never opens a recording: no `[BobcatScenario]` or `[BobcatSpec]` on it, and no `[assembly: RecordBobcatFeatures]` hook (the `Bobcat.Xunit` package applies that one for you). Every step records into nothing and the suite passes having produced no specification at all. **Nothing about a run can report this** -- a suite that records nothing is indistinguishable at run time from a suite with nothing to record -- which is why it is a compiler diagnostic |
| **BOBCAT029** (info) | A comment opening with `And` or `But` where no narrative is open: ordinary prose, not a step. English sentences begin "And …" constantly, so a keyword that can only *continue* a narrative must not be able to start one. Info rather than a warning, because the common case is that the comment really is prose -- it is here for the one author who meant a step and cannot see why it is missing |
| **BOBCAT032** (error) | A table column, or a partial object's member path, that names no member of the type it is about -- checked wherever the type and the text are both known at compile time: a constant table passed to a property check, `PartialObjects.FromTable`, a constant `Specified<T>.With` path. A stale column is always a broken spec |
| **BOBCAT033** (warning, MSBuild) | An xUnit v3 project with no `Microsoft.NET.Test.Sdk`: it runs from the command line and shows no tests in Rider or Visual Studio. Set `BobcatSuppressTestSdkWarning=true` if the suite only ever runs from the command line |
| **BOBCAT034** (error) | An `[EventModelRole]`'s `From`, or an `[EventModelTrigger]`'s `Route` or `RouteName`, names no parameter of the method it decorates (for `From`, nor a type parameter of it or of a type declaring it). Reported where the vocabulary is compiled, because at a call site in another assembly the same mistake is only a role silently never stamped. Write it with `nameof` |
| **BOBCAT038** (info) | A projected spec's HTTP act passes a `Route` or `RouteName` argument the generator can't turn into a route. This covers a helper call or a variable, a route name no endpoint attribute declares (a minimal API's `.WithName`), an ambiguous route name, or no route at all. The slice keeps its trigger kind with no route. Pass a constant template, an interpolated string, or a route name an endpoint attribute declares. See [The act's route](#act-route) |
| **BOBCAT036** (error) | Two projected specs in one compilation resolve to the same `{Feature}/{Scenario}` identity -- same-named methods in two classes bound to one feature, or two method names that read as the same sentence (`order_is_placed` and `order__is_placed`). Reported at each one, naming the other. Everything keyed on identity would treat them as one spec: filtering, the resident runner, the flake/duration ledger, Stoat's per-spec history, the Event Model's spec list. Rename one of the methods, or give one an [explicit name](#explicit-name) with `[BobcatSpec(Name = "…")]`. Compared exactly, case included, the way the runtime compares identities. See [Spec Identities](spec-identities.md#two-specs-one-identity) |
| **BOBCAT037** (error) | The project calls step helpers (or has projected assertions) for the generator to intercept, but `Bobcat.Generated` isn't in `InterceptorsNamespaces`. The `Bobcat.Generators` package sets it for you; a `ProjectReference` to the generator doesn't, because a project reference takes no build assets. Reported once, at the first call site, in place of a `CS9137` per call site in a generated file. Add `<InterceptorsNamespaces>$(InterceptorsNamespaces);Bobcat.Generated</InterceptorsNamespaces>`, or import the generator's `buildTransitive/Bobcat.Generators.props` the way `samples/Directory.Build.props` does. An error because it can't be wrong: it reads the same compiler option that would otherwise reject the interceptors. See [Getting Started](getting-started.md#referencing-bobcat-by-projectreference) |
| **BOBCAT039** (error) | A step helper the generated interceptor cannot call: `protected`, `private`, `private protected`, or declared on a type the assembly cannot reach (a private nested class). Reported once, at the helper, and its calls get no interceptor -- before this it was a CS0122 per call site in `BobcatStepInterceptors.g.cs`. Make the helper `public` or `internal` (`protected internal` works too) |

BOBCAT001–022, 030, 031 and 035 were retired with the Gherkin lane in 0.50.0 (bobcat#471). Nothing
emits them, and the ids are never reused.

The honest limit on BOBCAT029, pinned by
`Bobcat.Projected.Acceptance.Tests/MarkerCommentTighteningTests`: inside a test that is *already* narrating,
`And …` is taken at its word. There is no way to tell that sentence from a step, and guessing from
its wording would be worse than a rule an author can learn.

## The sample corpus

`src/Bobcat.Xunit.Samples` is Storyteller 5's own sample suites recreated one specification at a
time as ordinary xUnit v3 tests -- sentences, facts, output parameters, tables, sets, decision tables,
narrated tests, and the projected-assertion style. **Twenty-four of its forty-one specifications fail
on purpose**, because the samples exist to show what each outcome *looks* like.

```bash
dotnet build src/Bobcat.Xunit.Samples/Bobcat.Xunit.Samples.csproj
./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples
```

For the Critter Stack vocabulary on a real application, `samples/BankAccountES` runs its
specifications on `CritterStackSpec` against Marten and Fisher, with its own HTTP and message acts on
top. `src/Bobcat.Projected.Acceptance.Tests` and `src/Bobcat.CritterStackScenario.Tests` pin the
behaviour this page describes.
