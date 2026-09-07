using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issues #238, #239, #240 and #242 — four places where the emitted shape was right for the general
/// case and wrong for the case the model actually declared, all found by running an eleven-slice
/// chapter rather than by reading its output.
/// </summary>
public class SliceShapeTests
{
    private const string Model =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: ProposeHomeCheckAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: HomeCheckAssignmentAccepted }
            externalSystems:
              - { name: Volunteering, direction: Inbound }
            aggregates: [Appointment]
            events: [HomeCheckAppointmentProposed]
            elements:
              Appointment:
                fields: { ownerId: Guid, kind: string, status: string }
              HomeCheckAssignmentAccepted:
                fields: { assignmentId: Guid, ownerId: Guid, proposedFor: DateTimeOffset }
              HomeCheckAppointmentProposed:
                fields: { appointmentId: Guid, ownerId: Guid }
            specifications:
              feature: BookingAppointments
              scenarios:
                - name: Accepting a home check assignment proposes an appointment
                  when: { command: HomeCheckAssignmentAccepted }
                  then:
                    - event: HomeCheckAppointmentProposed
          - name: ConfirmAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: Http, label: My appointments }
            command: ConfirmAppointment
            aggregates: [Appointment]
            events: [AppointmentConfirmed]
            elements:
              ConfirmAppointment:
                fields: { appointmentId: Guid }
              AppointmentConfirmed:
                fields: { appointmentId: Guid, ownerId: Guid }
            specifications:
              feature: BookingAppointments
              scenarios:
                - name: A proposed appointment is confirmed
                  given: [ { event: HomeCheckAppointmentProposed } ]
                  when: { command: ConfirmAppointment }
                  then:
                    - event: AppointmentConfirmed
                - name: Confirming an already cancelled appointment is refused
                  given: [ { event: HomeCheckAppointmentProposed } ]
                  when: { command: ConfirmAppointment }
                  then:
                    - validationFails: "This appointment was cancelled"
          - name: AppointmentsQueue
            pattern: View
            domain: Appointments
            projections: [AppointmentsQueueProjection]
            readModels: [AppointmentsQueue]
            elements:
              AppointmentsQueue:
                fields: { ownerId: Guid, kind: string }
            specifications:
              feature: BookingAppointments
              scenarios:
                - name: A proposed home check waits in the queue
                  given: [ { event: HomeCheckAppointmentProposed } ]
                  then:
                    - readModel: AppointmentsQueue
                      contains: { Status: Proposed, AwaitingAction: "true" }
        """;

    private static CuratedModelFile model()
    {
        var reading = CuratedModelReader.Read(Model);
        reading.Succeeded.ShouldBeTrue(string.Join("; ", reading.Problems));
        return reading.File!;
    }

    private static string scaffold(string slice)
        => SliceScaffolder.Scaffold(model(), model().Slices.Single(x => x.Name == slice)).Values.Single();

    [Fact]
    public void a_slice_that_creates_the_stream_binds_no_write_model()
    {
        // Issue #239. The trigger carries the volunteering flow's ids — assignmentId, ownerId —
        // and nothing that could name an Appointment, because the appointment does not exist yet.
        // [WriteModel] resolves the stream id out of the incoming message, so binding one here is
        // not awkward, it is impossible: Wolverine refuses the DISPATCH, before the body runs, with
        // "Unable to determine an aggregate id for the parameter". Every other slice in a scaffold
        // fails with its own NotImplementedException naming the decision to make; these three used
        // to fail with a framework binding error that said nothing about the slice.
        var code = scaffold("ProposeHomeCheckAppointment");

        code.ShouldContain("public static IStartStream Handle(HomeCheckAssignmentAccepted trigger)");
        code.ShouldNotContain("[WriteModel]");
        code.ShouldContain("MartenOps.StartStream<Appointment>");
        code.ShouldContain("deterministic id makes the retry idempotent");
    }

    [Fact]
    public void a_slice_whose_command_carries_the_id_still_binds_one()
    {
        // The other side of the same rule, and the reason it takes positive evidence: ConfirmAppointment
        // carries an appointmentId, so the stream is addressable and this is a state change.
        var code = scaffold("ConfirmAppointment");

        code.ShouldContain("[WriteModel] Appointment? appointment");
        code.ShouldNotContain("IStartStream");
    }

    [Fact]
    public void a_read_model_gets_the_columns_the_model_names()
    {
        // Issue #240. Both sources were sitting there unused: the `elements:` hints, and the
        // columns the scenarios assert on — which the emitted TODO literally pointed at while
        // reading none of them. The read model was the one type in the scaffold that came out
        // empty no matter how well the model was curated.
        var code = scaffold("AppointmentsQueue");

        code.ShouldContain("public Guid OwnerId { get; set; }");
        code.ShouldContain("public string Kind { get; set; } = string.Empty;");
        code.ShouldContain("public string Status { get; set; } = string.Empty;");
        code.ShouldContain("public bool AwaitingAction { get; set; }");
    }

    [Fact]
    public void a_view_slice_arranges_against_the_stream_it_reads_not_an_invented_one()
    {
        // Issue #240, second half: a View slice has no write model, so synthesizing
        // `AppointmentsQueueModel` for its arrange step named a type nothing emits — BOBCAT011,
        // the #231 failure mode again. The events it arranges belong to a stream the model already
        // identifies, through the slices that declare them.
        var feature = SliceScaffolder.ScaffoldFeatures(model()).Values.Single();

        feature.ShouldContain("Given no events for Appointment");
        feature.ShouldNotContain("AppointmentsQueueModel");
    }

    [Fact]
    public void a_scaffolded_reference_type_property_carries_an_initializer()
    {
        // Issue #242. Under <Nullable>enable</Nullable> — the dotnet new default — an
        // uninitialized non-nullable reference property is CS8618, and a repo with
        // TreatWarningsAsErrors gets a red build out of a scaffold #226 established should be
        // green. Value types need nothing and must not get `= null!`.
        var aggregate = SliceScaffolder.ScaffoldAggregates(model()).Values.Single();

        aggregate.ShouldContain("public string Kind { get; set; } = string.Empty;");
        aggregate.ShouldContain("public Guid OwnerId { get; set; }");
        aggregate.ShouldNotContain("public Guid OwnerId { get; set; } = null!;");
    }
}
