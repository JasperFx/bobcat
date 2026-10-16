# Bobcat with NUnit

`Bobcat.NUnit` lets an [NUnit](https://nunit.org) suite publish itself as Bobcat specifications  -- 
the tests keep running on NUnit, and Bobcat renders, reports and tracks them as scenarios.

```bash
dotnet add package Bobcat.NUnit
```

Nothing is rewritten and the tests keep their own runner. For writing new specifications, see
[Writing Specifications](marker-steps.md).

## Wire it up

Put `[BobcatScenario]` on the test fixture, and name the feature it belongs to:

```csharp
using Bobcat;
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
| Ignored, Explicit, Skipped, Inconclusive, Not Runnable, Cancelled | nothing -- the scenario is withdrawn, because the body never made its assertions |

A specification whose `SpecAssert` checks gathered a wrong without throwing is still a red test:
the attribute throws from `AfterTest`, and NUnit records that against the test.

It is stateless -- NUnit builds one attribute instance per declaration and runs it around every test
it covers, in parallel when the fixture is `[Parallelizable]` -- so parallel fixtures are fine.

**`[BobcatScenario(Lifecycle = Lifecycle.Acceptance)]`** (bobcat#545) marks a spec written before its
behaviour is finished: a failure is recorded in full as `acceptance: failing` and NUnit reports the test
ignored, so the build stays green. See
[not yet passing](marker-steps.md#acceptance).

## Versions

Built against NUnit 4.3.2, and tested here on `NUnit3TestAdapter` 5.2. The package references the
NUnit framework only, never the adapter, so which runner and test platform you use is yours to
choose. See [Version Compatibility](/compatibility).

## What it renders

Everything Bobcat renders is the same whichever runner a test runs on, so all of it applies here --
see [Writing Specifications](marker-steps.md).

**One thing is not there yet.** An NUnit suite cannot be
[run by spec identity](spec-identities.md#listing-and-running-by-identity) today, so it cannot be
driven by [the resident runner](resident-runner.md) either. Listing works; Bobcat refuses to turn an
identity into an NUnit filter until that filter's spelling is verified against a real host, because
an unverified filter is exactly the run that looks filtered and is not.

## See also

- [Bobcat with xUnit.net](xunit.md), [Bobcat with TUnit](tunit.md), [Bobcat with MSTest](mstest.md) -- the same thing, other runners
- [Writing Specifications](marker-steps.md) -- step helpers, marker comments, `SpecAssert` and the scenario vocabularies
