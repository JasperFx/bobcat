using Bobcat.MSTest;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Bobcat.MSTest.Tests.Specs;

/// <summary>
/// The <c>docs/mstest.md</c> wiring example: <c>[BobcatFeature]</c> names the feature and
/// <c>[BobcatScenario]</c> chooses the class for the global hooks to bracket; marker comments are the
/// steps. Green.
/// </summary>
// #region mstest-wiring
[TestClass, BobcatFeature("Booking"), BobcatScenario]
public class BookingSpecs
{
    private readonly Dictionary<Guid, string> _appointments = new();

    [TestMethod]
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
// #endregion mstest-wiring
