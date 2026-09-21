# Bobcat with TUnit

`Bobcat.TUnit` lets a [TUnit](https://tunit.dev) suite publish itself as Bobcat specifications —
the tests keep running on TUnit, and Bobcat renders, reports and tracks them as scenarios.

```bash
dotnet add package Bobcat.TUnit
```

This is the projected lane. Nothing is rewritten and the tests keep their own runner. For writing
new specifications on Bobcat's own engine instead, see
[Specifications with Code](tutorials/specifications-with-code.md).

## Wire it up

Put `[BobcatScenario]` on the test class, and name the feature it belongs to:

```csharp
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

It works on a single method too, when only part of a class should project.

## What the attribute does

Unlike the xUnit adapter, this one implements TUnit's `ITestStartEventReceiver` and
`ITestEndEventReceiver` rather than deriving from a base attribute. The effect is identical: a
scenario opens around each test and closes with **the verdict TUnit reported** — a failing test is
published as a failure, a skipped one is withdrawn.

## Use this package rather than writing your own

The adapter carries four things only Bobcat knows — the verdict, run-id and `BOBCAT_RUN_TAG`
attribution, the `RunStarted`/`RunFinished` pair, and the interceptor opt-in. A hand-rolled
adapter that gets the verdict wrong publishes **every scenario as a clean pass**, including the
failing ones, and a suite in that state looks exactly like a correct one. That has happened; see
[Bobcat with xUnit.net](xunit.md) for the full account.

## Turning the test into a readable specification

The attribute publishes the test. Marker comments and `[BobcatStep]` helpers are what make it
*read* as a specification — see [Specs From Existing Tests](marker-steps.md).

## See also

- [Bobcat with xUnit.net](xunit.md) — the same lane, other runner
- [Specifications with Code](tutorials/specifications-with-code.md) — choosing between the two code lanes
