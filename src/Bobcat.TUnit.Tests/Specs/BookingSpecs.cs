using Bobcat.TUnit;

namespace Bobcat.TUnit.Tests.Specs;

/// <summary>
/// The <c>docs/tunit.md</c> wiring example: <c>[BobcatFeature]</c> names the feature and
/// <c>[BobcatScenario]</c> opens the recording; marker comments are the steps. Green.
/// </summary>
// #region tunit-wiring
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
        await Assert.That(_appointments[id]).IsEqualTo("Confirmed");
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
// #endregion tunit-wiring
