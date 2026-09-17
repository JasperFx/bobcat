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
"Given the events are published on 3 threads".

The two compose. Comments give a test its narrative; decorated helpers give real per-step timing
across every test in the suite that touches them.

### A placeholder binds from the value, not from the syntax (issue #339)

A literal argument is substituted at build time. Everything else binds **at execution time**, from
the value the helper was actually called with — which matters because a typed vocabulary has
almost no literals in it:

```csharp
[BobcatStep("{aggregate} \"{id}\" has already recorded these events", Keyword = "Given")]
internal Task GivenEvents(Type aggregate, Guid id) => …

[BobcatStep("{command} is posted to \"{route}\"", Keyword = "When")]
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
