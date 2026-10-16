# Bobcat with TUnit

`Bobcat.TUnit` lets a [TUnit](https://tunit.dev) suite publish itself as Bobcat specifications  -- 
the tests keep running on TUnit, and Bobcat renders, reports and tracks them as scenarios.

```bash
dotnet add package Bobcat.TUnit
```

Nothing is rewritten and the tests keep their own runner. For writing new specifications, see
[Writing Specifications](marker-steps.md).

## Wire it up

Put `[BobcatScenario]` on the test class, and name the feature it belongs to:

```csharp
using Bobcat;
using Bobcat.TUnit;

[BobcatFeature("Booking"), BobcatScenario]
public class BookingSpecs
{
    [Test]
    public async Task a_proposed_appointment_is_confirmed()
    {
        // Given a proposed appointment
        var id = await Propose();

        // When it is confirmed
        await Confirm(id);

        // Then the appointment is confirmed
        (await Load(id)).Status.ShouldBe(Status.Confirmed);
    }
}
```

It works on a single method too, when only part of a class should project. `[BobcatSpec]` and
`[BobcatTheory]` are xUnit attributes; on TUnit, bind a test to an Event Model slice with
`[BobcatSlice]` beside `[BobcatScenario]` (see [Writing Specifications](marker-steps.md#bobcatspec)).
Each `[Arguments]` row records as its own specification
([each data row is its own specification](spec-identities.md#each-data-row-is-its-own-specification)).

## What the attribute does

Unlike the xUnit adapter, this one implements TUnit's `ITestStartEventReceiver` and
`ITestEndEventReceiver` rather than deriving from a base attribute. The effect is identical: a
scenario opens around each test and closes with **the verdict TUnit reported** -- a failing test is
published as a failure, a skipped one is withdrawn.

**`[BobcatScenario(Lifecycle = Lifecycle.Acceptance)]`** (bobcat#545) marks a spec written before its
behaviour is finished: a failure is recorded in full as `acceptance: failing` and TUnit reports the test
skipped (`OverrideResult`), so the build stays green. See
[not yet passing](marker-steps.md#acceptance).

## Versions

Built against `TUnit.Core` 1.74.0. The package takes no test-platform reference; your own `TUnit`
package brings the platform. See [Version Compatibility](/compatibility).

## What it renders

Everything Bobcat renders is the same whichever runner a test runs on, so all of it applies here
too -- a [rendered specification on the console](marker-steps.md#reading-the-specification-the-run-produced),
[expected/actual cells](marker-steps.md#checks-that-gather-instead-of-throwing) through `SpecAssert`,
[tables and sets from a table literal](marker-steps.md#tables-and-sets-from-a-c-test), and
[projected Shouldly assertions](marker-steps.md#projecting-the-assertions-you-already-wrote-opt-in).

**One thing is TUnit-specific.** A TUnit suite is [run by spec identity](spec-identities.md#listing-and-running-by-identity)
through the platform's tree-node filter, and that filter can only name several methods of one class,
or one method name across several classes, in a single run. [The resident runner](resident-runner.md)
refuses a cold command for any other set rather than run tests nobody asked for; a warm command runs
any set.

## Use this package rather than writing your own

The adapter carries things only Bobcat knows -- the verdict, run-id and `BOBCAT_RUN_TAG`
attribution, and the `RunStarted`/`RunFinished` pair, closed once the session's last scenario ends. A hand-rolled
adapter that gets the verdict wrong publishes **every scenario as a clean pass**, including the
failing ones, and a suite in that state looks exactly like a correct one. That has happened; see
[Bobcat with xUnit.net](xunit.md) for the full account.

## Turning the test into a readable specification

The attribute publishes the test. Marker comments and decorated step helpers are what make it
*read* as a specification -- see [Writing Specifications](marker-steps.md).

## See also

- [Bobcat with xUnit.net](xunit.md) -- the same thing, other runner
- [Writing Specifications](marker-steps.md) -- step helpers, marker comments, `SpecAssert` and the scenario vocabularies
