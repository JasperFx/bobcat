# Specs from tests you already have

Issue #110. The other two authoring styles ask you to write a specification: a `.feature` file
bound to a fixture, or a [code-first `Specification`](code-first-specs.md). This one asks for
almost nothing. You point Bobcat at tests that already exist, in whatever runner they already use,
and they start reporting themselves as specifications — ordered steps, a `{Feature}/{Scenario}`
identity, and live progress in the [viewer](monitor-design.md).

The constraint that shaped it: a large existing suite has to be able to adopt this **one class at
a time**, without a base class, a signature change, or a rewrite. Anything more expensive than
that and the answer is always "not this quarter".

Marten's `DaemonTests` is the first real subject ([JasperFx/marten#5363](https://github.com/JasperFx/marten/pull/5363)) --
eighteen tests, no test body edited.

## Two ways in, and they compose

**Marker comments** declare the steps of one test:

```csharp
[Bobcat.BobcatFeature("Async daemon")]
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

**`[BobcatStep]`** goes the other way. Decorate a shared helper once and *every* test that already
calls it renders that step:

```csharp
[BobcatStep("the events are published on {threads} threads", Keyword = "Given")]
internal Task PublishMultiThreaded(int threads) => …
```

`{threads}` is filled in from the call site, so one attribute renders `PublishMultiThreaded(3)` as
"Given the events are published on 3 threads". Only literal arguments are substituted — a step
reading "published on threadCount threads" would be worse than one that visibly did not resolve.

The two compose. Comments give a test its narrative; decorated helpers give real per-step timing
across every test in the suite that touches them.

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

### `[BobcatStep]` helpers cannot be `protected`

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

## Declared is not executed

Two different things, kept apart on purpose:

- **Declared steps** are what the test *says* it does, read from marker comments at compile time
  and known before a line of it runs. That is what lets a scenario announce "step 2 of 4" up
  front, and it is trustworthy precisely because it was never inferred from what happened.
- **Recorded steps** are what actually ran, with a duration and a verdict. They come from
  `[BobcatStep]` interceptors.

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

## Binding a projected test to a slice — `[BobcatSlice]` (issue #324)

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
code-first and projected specifications at once, because a slice is a vertical behaviour and not an
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

## The honest limits

- **A comment-declared step still has no clock of its own.** It reports the work observed *inside*
  it (issue #304) — the decorated helpers that ran under it, their verdicts, and the sum of their
  durations — and a region containing none of them says nothing at all. Timing needs somewhere to
  intercept and a comment does not give one; what changed is that the work underneath it is now
  attributed to it, exactly, from the call site's line.
- **A step outside a scenario is silent.** Decorated helpers get called from plenty of places that
  are not specifications, and reporting from them would be noise.
- **Test methods are matched by attribute name** — `Fact`, `Theory`, `Test`, `TestCase` — so the
  generator needs no reference to a runner it is trying to stay neutral about.
