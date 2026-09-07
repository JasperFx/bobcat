using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #239: a slice that starts a stream is scaffolded with something that can start one —
/// and, just as load-bearing, a slice that does not is left alone.
/// </summary>
/// <remarks>
/// <c>[WriteModel]</c> resolves the stream id out of the incoming message, so an automation whose
/// trigger carries an upstream flow's ids failed at <b>dispatch</b> — "Unable to determine an
/// aggregate id for the parameter 'appointment'" — a framework error saying nothing about the
/// slice, where every other unfilled slice fails on its own named TODO.
///
/// Deriving it takes two signals and positive evidence for each; the rules that do not work are
/// the interesting part, and each has a test below.
/// </remarks>
public class CreatingSliceTests
{
    internal const string ModelYaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          # Creating: the trigger carries the volunteering flow's ids and nothing that names an
          # Appointment, and no scenario arranges history.
          - name: ProposeHomeCheckAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Home check assignment accepted }
            aggregates: [Appointment]
            events: [HomeCheckAppointmentProposed]
            messages: [AppointmentProposalNotice]
            externalSystems:
              - { name: Notifications, direction: Outbound }
            elements:
              HomeCheckAssignmentAccepted:
                fields: { assignmentId: Guid, ownerId: Guid, proposedFor: DateTimeOffset }
            specifications:
              feature: Appointments
              scenarios:
                - name: An accepted assignment proposes an appointment
                  then: [{ event: HomeCheckAppointmentProposed }]
          # Not creating: the trigger names the appointment, so [WriteModel] can bind it.
          - name: CancelAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Home check cancelled }
            aggregates: [Appointment]
            events: [AppointmentCancelled]
            elements:
              HomeCheckCancelled:
                fields: { appointmentId: Guid, reason: string }
            specifications:
              feature: Appointments
              scenarios:
                - name: A cancelled home check cancels the appointment
                  then: [{ event: AppointmentCancelled }]
          # Not creating: the scenario arranges history, so the stream is already there. This is
          # the computed-identity shape — a request with no id field that still addresses an
          # existing stream, which is why "the act carries no id" alone is not enough.
          - name: CompleteAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Home check completed }
            aggregates: [Appointment]
            events: [AppointmentCompleted]
            elements:
              HomeCheckCompleted:
                fields: { visitId: Guid, completedAt: DateTimeOffset }
            specifications:
              feature: Appointments
              scenarios:
                - name: A completed home check completes the appointment
                  given:
                    - { event: HomeCheckAppointmentProposed, with: { ownerId: "{streamId}" } }
                  then: [{ event: AppointmentCompleted }]
          # Not creating: the model says nothing about the act's fields. This is every slice of a
          # board export, which is why "no scenario arranges history" alone is not enough.
          - name: ArchiveAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Retention window elapsed }
            aggregates: [Appointment]
            events: [AppointmentArchived]
            specifications:
              feature: Appointments
              scenarios:
                - name: An elapsed retention window archives the appointment
                  then: [{ event: AppointmentArchived }]
        """;

    private static CuratedModelFile model()
    {
        var reading = CuratedModelReader.Read(ModelYaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private static string codeFor(string sliceName)
    {
        var file = model();
        return SliceScaffolder.Scaffold(file, file.Slices.Single(x => x.Name == sliceName)).Single().Value;
    }

    private static bool creates(string sliceName)
    {
        var file = model();
        return SliceScaffolder.CreatesTheStream(file, file.Slices.Single(x => x.Name == sliceName));
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

        creates("ProposeHomeCheckAppointment").ShouldBeTrue();
    }

    [Fact]
    public void an_act_that_names_the_aggregate_binds_a_write_model()
    {
        // [WriteModel] resolves the id out of the incoming message, and this trigger carries one.
        codeFor("CancelAppointment")
            .ShouldContain("public static EventsToAppend Handle(HomeCheckCancelled trigger, [WriteModel] Appointment appointment)");

        creates("CancelAppointment").ShouldBeFalse();
    }

    [Fact]
    public void arranged_history_beats_a_missing_id_field()
    {
        // The computed-identity shape this scaffold itself teaches — `[Identity] public Guid ...Id
        // => …` — has no id field either, and it addresses a stream that exists. History is what
        // separates it from a slice that genuinely creates, which is why "the act carries no id"
        // cannot decide this alone.
        codeFor("CompleteAppointment")
            .ShouldContain("public static EventsToAppend Handle(HomeCheckCompleted trigger, [WriteModel] Appointment appointment)");

        creates("CompleteAppointment").ShouldBeFalse();
    }

    [Fact]
    public void a_model_that_says_nothing_about_the_act_is_not_read_as_creating()
    {
        // The emlang-import case, and the reason "no scenario arranges history" cannot decide
        // this alone: a board export whose tests declare no prior events carries no `given:`
        // anywhere, so that rule alone turns every slice in the model into a creating slice.
        //
        // Silence is not evidence. A wrongly bound [WriteModel] fails loudly and accurately at
        // dispatch; a wrongly emitted StartStream quietly creates a second stream per message,
        // forever. The rule leans the safe way.
        codeFor("ArchiveAppointment").ShouldContain("[WriteModel] Appointment appointment");

        creates("ArchiveAppointment").ShouldBeFalse();
    }
}
