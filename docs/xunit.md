# Bobcat with xUnit.net

`Bobcat.Xunit` lets an **xUnit v3** suite you already have publish itself as Bobcat specifications —
the tests keep running on xUnit, and Bobcat renders, reports and tracks them as scenarios.

```bash
dotnet add package Bobcat.Xunit
```

This is the projected lane. Nothing is rewritten, nothing moves, and the tests keep their own
runner. For writing new specifications on Bobcat's own engine instead, see
[Specifications with Code](tutorials/specifications-with-code.md).

## Wire it up

Put `[BobcatScenario]` on the test class, and name the feature it belongs to:

```csharp
using Bobcat.Xunit;

[BobcatFeature("Async daemon"), BobcatScenario]
public class DaemonSpecs
{
    [Fact]
    public async Task the_daemon_catches_up()
    {
        // Given events are published
        await PublishEvents(10);

        // When the daemon runs
        await RunDaemon();

        // Then the projection is caught up
        (await Projection()).Count.ShouldBe(10);
    }
}
```

`[BobcatScenario]` works on a single method too, when only part of a class should project.

The scenario's title is the method name, so the method name is user-facing text here in a way it is
not in an ordinary test.

## What the attribute does

It derives from xUnit v3's `BeforeAfterTestAttribute`, opening a scenario around each test and
closing it with **the verdict xUnit reported** — a failing test is published as a failure, and a
skipped one is withdrawn rather than reported as anything at all.

## What the lane renders now

With the adapter in place, a projected suite does rather more than publish a verdict. All of it is
covered in [Specs From Existing Tests](marker-steps.md); the short list, so you know what to look
for:

| | |
|---|---|
| [A rendered specification on the console](marker-steps.md#reading-the-specification-the-run-produced) | on by default in a terminal with no console listening; `BOBCAT_SPEC_CONSOLE` overrides in both directions, and `BOBCAT_SPEC_PREVIEW=1 ./MySpecs --list-tests` previews without running |
| [Expected/actual cells](marker-steps.md#checks-that-gather-instead-of-throwing) | `SpecAssert.Check(name, actual, expected)` records instead of throwing, so a specification shows *every* disagreement rather than only its first. The adapter turns the gathered wrongs into the test's verdict at the end |
| [Tables and sets](marker-steps.md#tables-and-sets-from-a-c-test) | a table literal in the test — pipe-delimited text that `StepTable` reads by an implicit conversion — so one grammar body serves a `.feature` file and a test, and both lanes render the same grid |
| [Projected assertions](marker-steps.md#projecting-the-assertions-you-already-wrote-opt-in) | with `<BobcatProjectAssertions>true</BobcatProjectAssertions>`, ordinary statement-level Shouldly calls render as steps and a *run* of them is all evaluated before the next action |
| [Listing and running one specification](spec-identities.md#listing-and-running-by-identity) | `BOBCAT_LIST_SPECS` writes what the suite specifies; an identity is translated to `--filter-method Ns.Class.method`, which is what [the resident runner](resident-runner.md) drives |

One step-attribute family covers both lanes: `[Given]`, `[When]`, `[Then]`, `[Check]` and the
keywordless `[Step]` are matched against a `.feature` file on a fixture *and* intercepted at the call
site when a test calls the method directly. `[BobcatStep]` is the legacy spelling of the same thing.

::: tip The sample corpus
`src/Bobcat.Xunit.Samples` recreates Storyteller 5's own sample suites as ordinary xUnit v3 tests —
sentences, facts, output parameters, tables, sets, decision tables and narrated tests. Twenty-four of
its forty-one specifications **fail on purpose**, because the samples exist to show what each outcome
looks like.
:::

## Use this package rather than writing forty lines

Bobcat deliberately shipped no adapter at first, on the theory that forty lines were cheaper than
guessing at a runner. That was wrong, and the reason is worth stating because the failure is
invisible: those forty lines carry four things only Bobcat knows, and getting any of them wrong
leaves a green suite looking exactly like a correct one.

Marten's hand-rolled copy got all four wrong:

- It never set the verdict, so **every scenario it ever published was a clean pass** — including
  the failing ones.
- It minted its own run id and dropped `BOBCAT_RUN_TAG`, so its evidence could not be attributed to
  whatever asked for the run.
- It published a `RunStarted` it might not own, and never a `RunFinished`.
- Its interceptor opt-in was a csproj line a sibling project also needed — where forgetting it is a
  `CS9137` build failure rather than a missing feature.

## xUnit v2 is not supported, and is not waiting for an adapter

This is a structural limit rather than a backlog item. v2's `BeforeAfterTestAttribute` is
`Before(MethodInfo)` / `After(MethodInfo)` with no test context anywhere — **there is no verdict to
read at that point at all**. Reporting one would mean replacing the test framework rather than
hooking it.

v3 and TUnit both hand the result over at the end of a test, which is exactly what makes their
adapters small.

## Turning the test into a readable specification

The attribute publishes the test. Marker comments and decorated step helpers are what make it
*read* as a specification — a `// Given …` comment becomes a step, and a decorated shared helper
declares its step once for every test that calls it.

That is the larger subject: [Specs From Existing Tests](marker-steps.md).

## See also

- [Bobcat with TUnit](tunit.md) — the same lane, other runner
- [Specifications with Code](tutorials/specifications-with-code.md) — choosing between the two code lanes
