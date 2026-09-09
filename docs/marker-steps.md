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

## What you have to add

| | |
|---|---|
| Packages | `Bobcat` and `Bobcat.Generators` |
| One csproj line | `<InterceptorsNamespaces>$(InterceptorsNamespaces);Bobcat.Generated</InterceptorsNamespaces>` |
| A runner adapter | ~40 lines, see below |

The `InterceptorsNamespaces` line is only needed for `[BobcatStep]`. Marker comments reach the
runtime through a module initializer instead, which is why opting in really is only comments --
nothing in a test body could have been made to carry them, and an assembly with no marked class
gains no initializer and no startup cost.

### `[BobcatStep]` helpers cannot be `protected`

A C# interceptor must be an extension method, and an extension method cannot see a `protected`
member. So every decorated helper has to be `internal` or `public`. This is a language rule rather
than a Bobcat design choice, and it is the one real cost of the attribute half — widening eight
helpers on `DaemonContext` is exactly what Marten's PR did. Marker comments have no such
constraint.

### The runner adapter

Bobcat does not ship one yet, deliberately: the attributes here reference no test framework, and
guessing at xUnit v2 vs v3 vs TUnit vs NUnit in the core package would be worse than the forty
lines you write once. For xUnit v3:

```csharp
public sealed class BobcatScenarioAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        => _recording = ScenarioRecorder.Begin(feature, scenario, Sink.Value, RunId);

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        _recording.Failure = …;   // the runner's verdict, not ours
        _recording.Dispose();
    }
}
```

Marten's copy is in
[`src/DaemonTests/TestingSupport/BobcatScenarioAttribute.cs`](https://github.com/JasperFx/marten/blob/master/src/DaemonTests/TestingSupport/BobcatScenarioAttribute.cs)
and is a reasonable thing to paste.

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

A scenario publishes `ScenarioStarted` (with the declared step count when it has one),
`StepStarted` as each step **opens**, `StepFinished` with that step's own verdict, and
`ScenarioFinished`. Steps are announced when they open rather than when they end, because a
watcher looking at a run in flight needs the step that is currently taking the time — which is
exactly the one that has not finished yet.

With no viewer listening the publisher is null and the whole thing costs a few strings per test.

## The honest limits

- **A comment-declared step has no duration.** Timing needs somewhere to intercept, and a comment
  does not give one — only a decorated helper does. Comments give you the narrative and the
  count; `[BobcatStep]` gives you the clock. The line numbers are carried for the per-step verdict
  work still open on #110.
- **A step outside a scenario is silent.** Decorated helpers get called from plenty of places that
  are not specifications, and reporting from them would be noise.
- **Test methods are matched by attribute name** — `Fact`, `Theory`, `Test`, `TestCase` — so the
  generator needs no reference to a runner it is trying to stay neutral about.
