using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #235: a scenario can say "the stream this scenario runs against", and a scenario that
/// does not is told so.
/// </summary>
/// <remarks>
/// A collapsed endpoint computes its stream from the request body, and the act step builds that
/// body from the scenario's table and nothing else — so before <c>{streamId}</c> the act always
/// addressed a different stream than the <c>Given</c> events reached. The happy paths passed
/// anyway (the endpoint starts a fresh stream, and <c>Then X is emitted</c> reads the tracked
/// session), and so did the refusals, for the worst possible reason: the guard saw an empty
/// aggregate and refused nothing, so "confirming a cancelled appointment is refused" was green
/// whether or not the guard existed.
/// </remarks>
public class ScenarioStreamIdTests
{
    internal const string ModelYaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: ConfirmAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: Http, label: Appointment detail }
            command: ConfirmAppointment
            aggregates: [Appointment]
            events: [AppointmentConfirmed, HomeCheckAppointmentProposed]
            specifications:
              feature: Appointments
              scenarios:
                - name: A proposed appointment is confirmed
                  given:
                    - { event: HomeCheckAppointmentProposed, with: { ownerId: "{streamId}" } }
                  when: { command: ConfirmAppointment, with: { appointmentId: "{streamId}" } }
                  then: [{ event: AppointmentConfirmed, with: { appointmentId: "{streamId}" } }]
                - name: Confirming a cancelled appointment is refused
                  given:
                    - { event: HomeCheckAppointmentProposed }
                  when: { command: ConfirmAppointment, with: { note: "late" } }
                  then: [{ validationFails: "this appointment was cancelled" }]
        """;

    private static string feature()
    {
        var reading = CuratedModelReader.Read(ModelYaml);
        reading.Problems.ShouldBeEmpty();
        return SliceScaffolder.ScaffoldAll(reading.File!)["Features/Appointments.feature"];
    }

    private static string scenario(string name)
    {
        var text = feature();
        var start = text.IndexOf($"Scenario: {name}", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1);
        var end = text.IndexOf("  @slice:", start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    [Fact]
    public void the_token_expands_to_the_id_the_arrange_step_established()
    {
        var body = scenario("A proposed appointment is confirmed");

        // The one id the Given establishes, wherever the scenario names it.
        var id = body.Split('"')[1];
        Guid.TryParse(id, out _).ShouldBeTrue();

        body.ShouldContain($"Given no events for Appointment \"{id}\"");
        body.ShouldNotContain(SliceScaffolder.StreamIdToken);

        // Arrange row, act body and expected event all address that same stream — which is the
        // whole point: the endpoint's [Identity] now computes to the stream the Given wrote to.
        body.Split('\n').Count(line => line.Contains($"| {id} |")).ShouldBe(3);
    }

    [Fact]
    public void a_stream_id_column_is_typed_as_a_guid_not_as_the_literal_token()
    {
        var reading = CuratedModelReader.Read(ModelYaml);
        var files = SliceScaffolder.ScaffoldAll(reading.File!);

        // "{streamId}" parses as no sample value at all, so the inference would otherwise fall
        // through to string and the request would not bind an [Identity] Guid.
        files["Appointments/ConfirmAppointment.cs"].ShouldContain("public record ConfirmAppointment(Guid AppointmentId");
        files["Appointments/ConfirmAppointment.cs"].ShouldContain("public record HomeCheckAppointmentProposed(Guid OwnerId);");
    }

    [Fact]
    public void a_scenario_that_arranges_history_and_names_no_stream_is_warned_in_the_feature()
    {
        var body = scenario("Confirming a cancelled appointment is refused");

        body.ShouldContain("# WARNING: this scenario arranges history, but the act names no stream");
        body.ShouldContain(SliceScaffolder.StreamIdToken);
    }

    [Fact]
    public void the_happy_path_that_named_its_stream_is_not_warned()
        => scenario("A proposed appointment is confirmed").ShouldNotContain("WARNING");
}
