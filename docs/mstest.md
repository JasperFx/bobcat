# Bobcat with MSTest

`Bobcat.MSTest` lets an [MSTest](https://learn.microsoft.com/dotnet/core/testing/unit-testing-mstest-intro)
suite publish itself as Bobcat specifications -- the tests keep running on MSTest, and Bobcat
renders, reports and tracks them as scenarios.

```bash
dotnet add package Bobcat.MSTest
```

Nothing is rewritten and the tests keep their own runner. For writing new specifications, see
[Writing Specifications](marker-steps.md).

## Wire it up

Put `[BobcatScenario]` on the test class, and name the feature it belongs to:

```csharp
using Bobcat.MSTest;

[TestClass, BobcatFeature("Booking"), BobcatScenario]
public class BookingSpecs
{
    [TestMethod]
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

It works on a single method too, when only part of a class should project. `[DataRow]` rows each
open their own scenario. `[TestClass]` and `[TestMethod]` stay exactly as they are.

## What the attribute does -- and what does the work

MSTest has no per-test hook an attribute can implement, so this `[BobcatScenario]` only *chooses*
the test. The bracket runs from a `[GlobalTestInitialize]` / `[GlobalTestCleanup]` pair that the
package compiles into your test project, and that pair opens a scenario only for a test whose class
or method carries the attribute. That is why the package needs **MSTest 3.10 or later**.

The scenario closes with **the outcome MSTest recorded**:

| MSTest's outcome | What Bobcat publishes |
|---|---|
| Passed | a pass |
| Failed, Error, Timeout | a failure, carrying what the test threw |
| Inconclusive, Ignored, Aborted, NotRunnable, NotFound, InProgress, Unknown | nothing -- the scenario is withdrawn, because the body never made its assertions |

A specification whose `SpecAssert` checks gathered a wrong without throwing is still a red test:
the cleanup hook throws, and MSTest fails the test for it.

**Already have global hooks of your own?** Set `<BobcatMSTestHooks>false</BobcatMSTestHooks>` in the
project and call `Bobcat.MSTest.MSTestScenarioBracket.Open(...)` / `.Close(...)` from yours.

**`[BobcatScenario(Lifecycle = Lifecycle.Acceptance)]`** (bobcat#545) marks a spec written before its
behaviour is finished. On MSTest only a spec failed by gathered `SpecAssert` wrongs can be reported
inconclusive (a skip): a body that *throws* stays failed, because MSTest keeps the more important
outcome, and Bobcat records that one as a failure too. See
[not yet passing](marker-steps.md#not-yet-passing-lifecycle--lifecycleacceptance-bobcat545).

## Versions

The package references no MSTest assembly at all. MSTest 4 renamed its assemblies, so anything
compiled against one major fails to load under the other; the hooks the package adds are source,
compiled against whichever MSTest your project has. It is tested on MSTest 3.11 and checked on 4.5.

## What it renders

Everything Bobcat renders is the same whichever runner a test runs on, so all of it applies here --
see [Writing Specifications](marker-steps.md).

**One thing is not there yet.** An MSTest suite cannot be
[run by spec identity](spec-identities.md#listing-and-running-by-identity) today, so it cannot be
driven by [the resident runner](resident-runner.md) either. Listing works; Bobcat refuses to turn an
identity into an MSTest filter until that filter's spelling is verified against a real host.

## See also

- [Bobcat with xUnit.net](xunit.md), [Bobcat with TUnit](tunit.md), [Bobcat with NUnit](nunit.md) -- the same thing, other runners
- [Writing Specifications](marker-steps.md) -- step helpers, marker comments, `SpecAssert` and the scenario vocabularies
