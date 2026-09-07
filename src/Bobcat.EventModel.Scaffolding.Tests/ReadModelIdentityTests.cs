using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #236: a View slice whose document is keyed by something other than its stream is
/// scaffolded with the identity-bearing assertion, and one that is not keeps the shortcut.
/// </summary>
/// <remarks>
/// A single-stream projection's document id IS the stream id, which is what the shortcut step
/// assumes. A fan-out's is not — one document per owner, folding that owner's appointments from
/// every stream — so without <c>id:</c> the model can only describe half the read-model space,
/// and the pressure is to redefine a fan-out as single-stream so it can be tested.
/// </remarks>
public class ReadModelIdentityTests
{
    internal const string ModelYaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: ProposeAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: MessageHandler }
            command: ProposeAppointment
            aggregates: [Appointment]
            events: [AppointmentProposed]
          # Keyed by the appointment's own stream: the shortcut is exactly right.
          - name: AppointmentsQueue
            pattern: View
            domain: Appointments
            aggregates: [Appointment]
            projections: [AppointmentsQueueProjection]
            readModels: [AppointmentsQueue]
            specifications:
              feature: Appointments
              scenarios:
                - name: A proposed appointment joins the queue
                  given:
                    - { event: AppointmentProposed, with: { appointmentId: "{streamId}" } }
                  then: [{ readModel: AppointmentsQueue, contains: { status: proposed } }]
          # Keyed by the OWNER: one document folding that owner's appointments from every stream.
          - name: MyAppointments
            pattern: View
            domain: Appointments
            aggregates: [Appointment]
            projections: [MyAppointmentsProjection]
            fanOut: true
            readModels: [MyAppointments]
            specifications:
              feature: Appointments
              scenarios:
                - name: An owner sees their own appointments
                  given:
                    - { event: AppointmentProposed, with: { ownerId: "0e5e0001-0000-0000-0000-000000000001" } }
                  then:
                    - readModel: MyAppointments
                      id: "0e5e0001-0000-0000-0000-000000000001"
                      contains: { awaitingConfirmation: "2", confirmed: "1" }
        """;

    private static string feature()
    {
        var reading = CuratedModelReader.Read(ModelYaml);
        reading.Problems.ShouldBeEmpty();
        return SliceScaffolder.ScaffoldAll(reading.File!)["Features/Appointments.feature"];
    }

    [Fact]
    public void a_fan_out_read_model_is_asserted_by_its_own_id()
        => feature().ShouldContain(
            "Then the MyAppointments read model with id \"0e5e0001-0000-0000-0000-000000000001\" contains");

    [Fact]
    public void a_single_stream_read_model_keeps_the_shortcut()
    {
        var text = feature();
        text.ShouldContain("Then the AppointmentsQueue read model contains");
        text.ShouldNotContain("Then the AppointmentsQueue read model with id");
    }

    [Fact]
    public void the_id_understands_the_stream_id_token_like_any_other_scenario_value()
    {
        var yaml = ModelYaml.Replace("0e5e0001-0000-0000-0000-000000000001", SliceScaffolder.StreamIdToken);

        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();

        var text = SliceScaffolder.ScaffoldAll(reading.File!)["Features/Appointments.feature"];
        text.ShouldNotContain(SliceScaffolder.StreamIdToken);
        text.ShouldContain("Then the MyAppointments read model with id \"");
    }

    [Fact]
    public void an_id_on_anything_but_a_read_model_assertion_is_a_problem()
    {
        // `id:` says which document to load, so on an event or a refusal it is a mistake with
        // nowhere to go — and a silently ignored key is how a model comes to say one thing and
        // mean another.
        var yaml =
            """
            schema: 1
            model: CritterCrush
            slices:
              - name: ProposeAppointment
                pattern: Command
                events: [AppointmentProposed]
                specifications:
                  scenarios:
                    - name: A proposal is recorded
                      then: [{ event: AppointmentProposed, id: "nope" }]
            """;

        CuratedModelReader.Read(yaml).Problems
            .ShouldContain(x => x.Contains("`id:` names the read-model document to assert on"));
    }
}
