# Bobcat with NUnit

`Bobcat.NUnit` lets an [NUnit](https://nunit.org) suite publish itself as Bobcat specifications —
the tests keep running on NUnit, and Bobcat renders, reports and tracks them as scenarios.

```bash
dotnet add package Bobcat.NUnit
```

This is the projected lane. Nothing is rewritten and the tests keep their own runner. For writing
new specifications on Bobcat's own engine instead, see
[Specifications with Code](tutorials/specifications-with-code.md).

## Wire it up

Put `[BobcatScenario]` on the test fixture, and name the feature it belongs to:

```csharp
using Bobcat.NUnit;

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

It works on a single method too, when only part of a fixture should project. `[TestCase]` rows each
open their own scenario.

## What the attribute does

It is an NUnit `ITestAction` targeting each test, so on the fixture it runs around every test in it
rather than once around the fixture. A scenario opens in `BeforeTest` and closes in `AfterTest`
with **the result NUnit recorded**:

| NUnit's result | What Bobcat publishes |
|---|---|
| Passed, or passed with a warning | a pass |
| Failed, Error, a timeout, a setup or teardown failure | a failure, carrying NUnit's message (an unexpected exception's type and message are split back apart) |
| Ignored, Explicit, Skipped, Inconclusive, Not Runnable, Cancelled | nothing — the scenario is withdrawn, because the body never made its assertions |

A specification whose `SpecAssert` checks gathered a wrong without throwing is still a red test:
the attribute throws from `AfterTest`, and NUnit records that against the test.

It is stateless — NUnit builds one attribute instance per declaration and runs it around every test
it covers, in parallel when the fixture is `[Parallelizable]` — so parallel fixtures are fine.

## Versions

Built against NUnit 4.3.2 and checked against NUnit 4.6 and 5.0 with `NUnit3TestAdapter` 6.x. The
package references the NUnit framework only, never the adapter, so which runner and test platform
you use is yours to choose.

## What the lane renders now

Everything the projected lane does is lane-wide rather than per-runner, so all of it applies here —
see [Specs From Existing Tests](marker-steps.md).

**One thing is not there yet.** An NUnit suite cannot be
[run by spec identity](spec-identities.md#listing-and-running-by-identity) today, so it cannot be
driven by [the resident runner](resident-runner.md) either. Listing works; Bobcat refuses to turn an
identity into an NUnit filter until that filter's spelling is verified against a real host, because
an unverified filter is exactly the run that looks filtered and is not.

## See also

- [Bobcat with xUnit.net](xunit.md), [Bobcat with TUnit](tunit.md), [Bobcat with MSTest](mstest.md) — the same lane, other runners
- [Specifications with Code](tutorials/specifications-with-code.md) — choosing between the two code lanes
