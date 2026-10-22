using Bobcat.NUnit;
using NUnit.Framework;
using Shouldly;

namespace Bobcat.NUnit.Tests.Specs;

/// <summary>
/// The <c>docs/nunit.md</c> wiring example: <c>[BobcatFeature]</c> names the feature and
/// <c>[BobcatScenario]</c> opens the recording around each test; marker comments are the steps. Green.
/// </summary>
// #region nunit-wiring
[BobcatFeature("Booking"), BobcatScenario]
public class BookingSpecs
{
    private readonly Dictionary<Guid, string> _appointments = new();

    [Test]
    public async Task a_proposed_appointment_is_confirmed()
    {
        // Given a proposed appointment
        var id = await Propose();

        // When it is confirmed
        await Confirm(id);

        // Then the appointment is confirmed
        _appointments[id].ShouldBe("Confirmed");
    }

    private Task<Guid> Propose()
    {
        var id = Guid.NewGuid();
        _appointments[id] = "Proposed";
        return Task.FromResult(id);
    }

    private Task Confirm(Guid id)
    {
        _appointments[id] = "Confirmed";
        return Task.CompletedTask;
    }
}
// #endregion nunit-wiring
