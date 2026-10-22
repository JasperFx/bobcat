using Bobcat.Xunit.Samples.Application;
using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The <c>docs/marker-steps.md</c> slice-binding example: one class bound to a slice, and a method
/// that rebinds to another, which REPLACES the class's binding rather than overriding part of it.
/// Green.
/// </summary>
// #region bobcat-slice
[BobcatFeature("Booking appointments")]
[BobcatSlice(SliceType = typeof(ConfirmAppointment), Domain = "Scheduling", Chapter = "BookingAppointments")]
public class BookingSpecs
{
    private readonly SchedulingSteps _scheduling = new();

    [Fact]
    public async Task a_proposed_appointment_is_confirmed()
    {
        var id = Guid.Parse("8f1c2b7e-4d3a-4e5f-9a1b-2c3d4e5f6a7b");

        // #region value-placeholder-calls
        await _scheduling.GivenEvents(typeof(Appointment), id, new AppointmentProposed(id, "Ann"));
        await _scheduling.WhenPosted(new ConfirmAppointment(id), "/api/scheduling/confirmappointment");
        _scheduling.ThenEmitted(typeof(AppointmentConfirmed));
        // #endregion value-placeholder-calls
    }

    // One class, several slices: a method-level binding REPLACES the class's.
    [Fact]
    [BobcatSlice(SliceName = "ProposeHomeCheckAppointment", Pattern = "Automation")]
    public async Task an_accepted_assignment_proposes_a_visit()
    {
        var id = Guid.NewGuid();

        await _scheduling.WhenPosted(new ProposeHomeCheck(id, "Ann"), "/api/scheduling/proposehomecheck");
        _scheduling.ThenEmitted(typeof(AppointmentProposed));
    }
}
// #endregion bobcat-slice

/// <summary>
/// The <c>[BobcatSpec]</c> examples on <c>docs/marker-steps.md</c>, one attribute shape per method:
/// the three-attribute form and the one-attribute form side by side, an explicit scenario name, a
/// pending stub and an Acceptance spec. The Acceptance spec fails on purpose and is reported skipped;
/// the pending one is skipped; the rest are green.
/// </summary>
[BobcatFeature("Booking")]
public class ConfirmationSpecs
{
    private readonly SchedulingSteps _scheduling = new();

    private async Task aProposedAppointmentIsConfirmed()
    {
        var id = Guid.NewGuid();
        await _scheduling.GivenEvents(typeof(Appointment), id, new AppointmentProposed(id, "Ann"));
        await _scheduling.WhenPosted(new ConfirmAppointment(id), "/api/scheduling/confirmappointment");
        _scheduling.ThenEmitted(typeof(AppointmentConfirmed));
    }

    // #region bobcat-spec-before-after
    // Before: the test, the slice it binds, and the recording, in three attributes
    [Fact, BobcatScenario, BobcatSlice(SliceType = typeof(ConfirmAppointment))]
    public Task a_proposed_appointment_is_confirmed_the_long_way() => aProposedAppointmentIsConfirmed();

    // After: one
    [BobcatSpec(typeof(ConfirmAppointment))]
    public Task a_proposed_appointment_is_confirmed() => aProposedAppointmentIsConfirmed();
    // #endregion bobcat-spec-before-after

    // #region explicit-name
    [BobcatSpec(typeof(ConfirmAppointment), Name = "A volunteer can't re-confirm, twice")]
    public async Task volunteer_cannot_reconfirm()
    {
        var id = Guid.NewGuid();
        await _scheduling.GivenEvents(typeof(Appointment), id,
            new AppointmentProposed(id, "Ann"), new AppointmentConfirmed(id));

        await _scheduling.WhenPosted(new ConfirmAppointment(id), "/api/scheduling/confirmappointment");
        _scheduling.TheResponseIs(409);
    }
    // #endregion explicit-name

    // #region pending
    [BobcatSpec(typeof(ConfirmAppointment), Pending = true)]
    public Task a_rescheduled_appointment_keeps_its_volunteer()
        => throw new NotImplementedException();   // never runs
    // #endregion pending

    // #region acceptance
    [BobcatSpec(typeof(ConfirmAppointment), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_cancelled_appointment_cannot_be_confirmed()
    {
        var id = Guid.NewGuid();
        await _scheduling.GivenEvents(typeof(Appointment), id,
            new AppointmentProposed(id, "Ann"), new AppointmentCancelled(id));

        await _scheduling.WhenPosted(new ConfirmAppointment(id), "/api/scheduling/confirmappointment");
        _scheduling.TheResponseIs(409);   // not built yet: the endpoint still confirms it
    }
    // #endregion acceptance
}
