using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #321: a slice consuming a type it does not own must still see that type's shape.
/// </summary>
/// <remarks>
/// <c>elements:</c> is resolved per slice, which is right for generating a record — the emitting
/// slice owns its events' shapes. It is wrong for a decision about a type the slice does not own.
/// An Automation's trigger is by definition declared elsewhere, so the moment that trigger became
/// an in-model emission the consuming slice's field list went empty, the <c>actFields.Count == 0</c>
/// arm of <see cref="SliceScaffolder.CreatesTheStream"/> read that as "identifiable", and a
/// <c>StartStream</c> automation was scaffolded as a <c>[WriteModel]</c> bind that fails at dispatch.
///
/// Nothing about what the trigger carries changed. Only where it was written down.
/// </remarks>
public class TriggerShapeAcrossSlicesTests
{
    /// <summary>
    /// The CritterCrush shape once its Volunteering chapter came into the model
    /// (CritterStackSamples#21): the trigger is emitted here, and declared here.
    /// </summary>
    private const string Head =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: AcceptHomeCheckAssignment
            pattern: Command
            domain: Volunteering
            trigger: { kind: Http, label: Home Check Assigned }
            command: AcceptHomeCheckAssignment
            aggregates: [HomeCheck]
            events: [HomeCheckAssignmentAccepted]
            elements:
              AcceptHomeCheckAssignment:
                fields: { homeCheckId: Guid }
              HomeCheckAssignmentAccepted:
                fields: { assignmentId: Guid, ownerId: Guid, proposedFor: DateTimeOffset }
            specifications:
              feature: HomeChecks
              scenarios:
                - name: A volunteer accepts an assignment
                  given: [{ event: HomeCheckRequested }]
                  when: { command: AcceptHomeCheckAssignment, with: { homeCheckId: "{streamId}" } }
                  then: [{ event: HomeCheckAssignmentAccepted }]
          - name: ProposeHomeCheckAppointment
            pattern: Automation
            domain: Scheduling
            trigger: { kind: MessageHandler, label: HomeCheckAssignmentAccepted }
            aggregates: [Appointment]
            events: [HomeCheckAppointmentProposed]

        """;

    /// <summary>What the consuming automation declares locally. The seam is a whole line, so a
    /// raw-string margin has nothing to strip and the fixture cannot come out malformed.</summary>
    private const string LocalTriggerDeclaration =
        "    elements:\n      HomeCheckAssignmentAccepted:\n        fields: { appointmentId: Guid }\n";

    private const string Tail =
        """
            specifications:
              feature: BookingAppointments
              scenarios:
                - name: An accepted assignment proposes a visit
                  then: [{ event: HomeCheckAppointmentProposed }]
        """;

    private static string modelYaml(string automationElements = "") => Head + automationElements + Tail;

    private static CuratedModelFile model(string yaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private static CuratedSlice automation(CuratedModelFile file) =>
        file.Slices.Single(x => x.Name == "ProposeHomeCheckAppointment");

    [Fact]
    public void a_trigger_declared_by_the_emitting_slice_still_shapes_the_consumer()
    {
        var file = model(modelYaml());
        var slice = automation(file);

        // Positive evidence that the consumer declares nothing itself — otherwise this test would
        // be asserting the easy case and calling it the hard one.
        slice.Elements.ShouldBeEmpty();

        // The trigger carries an upstream flow's ids and nothing naming an Appointment, so this
        // slice creates the stream — whichever slice happens to have written the fields down.
        SliceScaffolder.CreatesTheStream(file, slice).ShouldBeTrue();

        var code = SliceScaffolder.Scaffold(file, slice).Single().Value;
        code.ShouldContain("public static StartStream Handle(HomeCheckAssignmentAccepted trigger)");
        code.ShouldNotContain("[WriteModel]");
    }

    [Fact]
    public void the_consuming_slice_does_not_emit_a_second_copy_of_the_contract()
    {
        var file = model(modelYaml());

        // The emitter owns the record. Finding its fields must not turn into declaring them: an
        // in-model trigger gets no inbound contract file at all (issue #223).
        SliceScaffolder.ScaffoldTriggerContracts(file).ShouldBeEmpty();
        SliceScaffolder.Scaffold(file, automation(file)).Single().Value
            .ShouldNotContain("record HomeCheckAssignmentAccepted");
    }

    [Fact]
    public void a_slices_own_declaration_still_wins_over_another_slices()
    {
        // Both slices declare the trigger, disagreeing. The consumer's own copy is the one that
        // shapes it — the fallback is a fallback, not a merge.
        var file = model(modelYaml(LocalTriggerDeclaration));
        var slice = automation(file);

        // Assert the fixture before the behaviour. A slice with NO local declaration also reports
        // false, so a fixture that failed to carry the declaration would pass for the wrong reason.
        slice.Elements.ShouldContainKey("HomeCheckAssignmentAccepted");

        // Its own declaration names an AppointmentId, so [WriteModel] can bind and this slice is
        // NOT creating — the opposite verdict from the same model, decided by the local copy.
        SliceScaffolder.CreatesTheStream(file, slice).ShouldBeFalse();
    }
}
