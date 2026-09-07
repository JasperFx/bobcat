using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #238: a refusal the model states over arranged history is scaffolded into a guard that
/// can actually see the state.
/// </summary>
/// <remarks>
/// <c>CollapsedEndpointFrame</c> emitted <c>Validate(TRequest request)</c> and then copied the
/// model's <c>validationFails:</c> into it as a TODO — and in a real chapter every one of those
/// refusals is about the aggregate: <em>this appointment was cancelled</em>, <em>this appointment
/// is already completed</em>. <c>request</c> cannot answer either, so the scaffolded signature
/// made the scaffolded TODO impossible to fill.
///
/// Which kind it is needs no new field: it is whether the refusing scenario arranged history.
/// </remarks>
public class StatefulGuardTests
{
    internal const string ModelYaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          # State-dependent: the refusal is stated over two arranged events.
          - name: ConfirmAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: Http }
            command: ConfirmAppointment
            aggregates: [Appointment]
            events: [AppointmentConfirmed, AppointmentProposed, AppointmentCancelled]
            specifications:
              feature: Appointments
              scenarios:
                - name: A proposed appointment is confirmed
                  given:
                    - { event: AppointmentProposed, with: { appointmentId: "{streamId}" } }
                  when: { command: ConfirmAppointment, with: { appointmentId: "{streamId}" } }
                  then: [{ event: AppointmentConfirmed }]
                - name: Confirming a cancelled appointment is refused
                  given:
                    - { event: AppointmentProposed, with: { appointmentId: "{streamId}" } }
                    - { event: AppointmentCancelled, with: { appointmentId: "{streamId}" } }
                  when: { command: ConfirmAppointment, with: { appointmentId: "{streamId}" } }
                  then: [{ validationFails: "This appointment was cancelled" }]
          # Request-shaped: nothing arranged, so the guard needs the command and nothing else.
          - name: ProposeAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: Http }
            command: ProposeAppointment
            aggregates: [Proposal]
            events: [AppointmentProposalRecorded]
            specifications:
              feature: Proposals
              scenarios:
                - name: A proposal in the past is refused
                  when: { command: ProposeAppointment, with: { proposedFor: "2020-01-01T00:00:00Z" } }
                  then: [{ validationFails: "A home check cannot be proposed in the past" }]
        """;

    private static string codeFor(string sliceName)
    {
        var reading = CuratedModelReader.Read(ModelYaml);
        reading.Problems.ShouldBeEmpty();
        return SliceScaffolder.Scaffold(reading.File!, reading.File!.Slices.Single(x => x.Name == sliceName))
            .Single().Value;
    }

    [Fact]
    public void a_refusal_stated_over_arranged_history_binds_the_aggregate()
    {
        var code = codeFor("ConfirmAppointment");

        code.ShouldContain(
            "public static ProblemDetails Validate(ConfirmAppointment command, [ReadModel] Appointment? appointment)");

        // And the TODO it must fill says which question it is answering.
        code.ShouldContain("// TODO guard: return new ProblemDetails { Detail = \"This appointment was cancelled\", Status = 400 };");
        code.ShouldContain("appointment's state, not the request's shape");
    }

    [Fact]
    public void a_refusal_about_the_request_alone_keeps_the_narrower_signature()
    {
        // The fix must not overreach: binding an aggregate a guard never reads is a load the
        // scenario did not ask for, and a parameter nobody uses reads as a mistake.
        var code = codeFor("ProposeAppointment");

        code.ShouldContain("public static ProblemDetails Validate(ProposeAppointment command)");
        code.ShouldNotContain("[ReadModel]");
    }

    [Fact]
    public void the_derivation_is_the_model_and_needs_no_new_field()
    {
        var model = CuratedModelReader.Read(ModelYaml).File!;

        SliceScaffolder.RefusesOnState(model.Slices.Single(x => x.Name == "ConfirmAppointment")).ShouldBeTrue();
        SliceScaffolder.RefusesOnState(model.Slices.Single(x => x.Name == "ProposeAppointment")).ShouldBeFalse();
    }
}
