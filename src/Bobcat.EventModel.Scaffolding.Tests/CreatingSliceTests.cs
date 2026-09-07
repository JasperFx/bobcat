using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #239: a slice that starts a stream is scaffolded with something that can start one.
/// </summary>
/// <remarks>
/// <c>WriteModelHandlerFrame</c> bound a non-nullable write model for every automation, and
/// <c>[WriteModel]</c> loads an <em>existing</em> stream — so a slice whose own scaffolded feature
/// said <c>Given no events for Appointment "…"</c> and nothing else was handed a handler demanding
/// an aggregate that cannot exist. The model already says which slices create: every scenario
/// bound to the slice arranges no prior events, the same fact the aggregate scaffolder acts on
/// when it makes the first event a <c>Create</c>.
/// </remarks>
public class CreatingSliceTests
{
    internal const string ModelYaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          # Creating: nothing is ever arranged, so there is no Appointment to load.
          - name: ProposeHomeCheckAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Home check assignment accepted }
            aggregates: [Appointment]
            events: [HomeCheckAppointmentProposed]
            messages: [AppointmentProposalNotice]
            externalSystems:
              - { name: Notifications, direction: Outbound }
            specifications:
              feature: Appointments
              scenarios:
                - name: An accepted assignment proposes an appointment
                  then: [{ event: HomeCheckAppointmentProposed }]
          # Appending: the scenario arranges the proposal, so the stream is already there.
          - name: CompleteAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Home check completed }
            aggregates: [Appointment]
            events: [AppointmentCompleted]
            specifications:
              feature: Appointments
              scenarios:
                - name: A completed home check completes the appointment
                  given:
                    - { event: HomeCheckAppointmentProposed, with: { ownerId: "{streamId}" } }
                  then: [{ event: AppointmentCompleted }]
          # Silent: no scenarios at all. Silence is not evidence, so nothing changes.
          - name: ArchiveAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Retention window elapsed }
            aggregates: [Appointment]
            events: [AppointmentArchived]
        """;

    private static string codeFor(string sliceName)
    {
        var reading = CuratedModelReader.Read(ModelYaml);
        reading.Problems.ShouldBeEmpty();
        return SliceScaffolder.Scaffold(reading.File!, reading.File!.Slices.Single(x => x.Name == sliceName))
            .Single().Value;
    }

    [Fact]
    public void a_creating_slice_starts_the_stream_and_binds_no_write_model()
    {
        var code = codeFor("ProposeHomeCheckAppointment");

        // A cascaded message rides along, so the tuple return has to survive the new shape.
        code.ShouldContain("public static (StartStream, AppointmentProposalNotice) Handle(HomeCheckAssignmentAccepted trigger)");
        code.ShouldNotContain("[WriteModel]");

        // And the shape it hands over is the one a filler needs — including the decision the
        // appending TODO never had to name: what the stream's id is.
        code.ShouldContain("//     var id = Guid.NewGuid();   // or the identity the trigger already carries");
        code.ShouldContain("//     return (Storage.StartStream<Appointment>(id, new HomeCheckAppointmentProposed(/* … */)), new AppointmentProposalNotice(/* … */));");
        code.ShouldContain("TODO: ProposeHomeCheckAppointment — decide which event starts the stream, and what its id is");
    }

    [Fact]
    public void a_slice_that_appends_to_arranged_history_keeps_its_write_model()
    {
        var code = codeFor("CompleteAppointment");

        code.ShouldContain("public static EventsToAppend Handle(HomeCheckCompleted trigger, [WriteModel] Appointment appointment)");
        code.ShouldNotContain("StartStream");
    }

    [Fact]
    public void a_slice_with_no_scenarios_is_not_read_as_creating()
    {
        // Vacuous truth over an empty scenario list would hand back a handler shape nobody can
        // fill on the strength of a model that said nothing at all.
        codeFor("ArchiveAppointment").ShouldContain("[WriteModel] Appointment appointment");

        var model = CuratedModelReader.Read(ModelYaml).File!;
        SliceScaffolder.CreatesTheStream(model.Slices.Single(x => x.Name == "ArchiveAppointment")).ShouldBeFalse();
        SliceScaffolder.CreatesTheStream(model.Slices.Single(x => x.Name == "ProposeHomeCheckAppointment")).ShouldBeTrue();
        SliceScaffolder.CreatesTheStream(model.Slices.Single(x => x.Name == "CompleteAppointment")).ShouldBeFalse();
    }
}
