using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #311: a curated <c>given:</c> can name a stream, so the fold a fan-out projection exists
/// for is expressible from the model.
/// </summary>
/// <remarks>
/// <para>
/// #236 gave the ASSERTION an id — a document keyed by an owner rather than by the scenario's
/// stream. This is the arrange half. Without it a curated scenario is single-stream by
/// construction, so "two appointments on different streams fold into one owner's document" — the
/// whole reason the projection is multi-stream — could not be asked for, and the sample that found
/// this carried it as a hotspot instead of a spec.
/// </para>
/// <para>
/// Shipped Gherkin could always express it: <c>Given no events for {aggregate} {string}</c>
/// re-points the stream and deletes nothing. The gap was the curated format and its scaffolder.
/// </para>
/// </remarks>
public class MultiStreamArrangeTests
{
    private const string ModelYaml =
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
                - name: Two appointments of one owner fold into one document
                  given:
                    - { event: AppointmentProposed, with: { ownerId: "0e5e0001-0000-0000-0000-000000000001" } }
                    - event: AppointmentProposed
                      stream: the second appointment
                      with: { ownerId: "0e5e0001-0000-0000-0000-000000000001" }
                  then:
                    - readModel: MyAppointments
                      id: "0e5e0001-0000-0000-0000-000000000001"
                      contains: { awaitingConfirmation: "2" }
        """;

    private static string feature(string yaml = ModelYaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();
        return SliceScaffolder.ScaffoldAll(reading.File!)["Features/Appointments.feature"];
    }

    /// <summary>The ids in a scaffolded feature, in the order the steps establish them.</summary>
    private static string[] streams(string text) =>
        text.Split('\n')
            .Where(line => line.Contains("no events for Appointment \""))
            .Select(line => line.Split('"')[1])
            .ToArray();

    [Fact]
    public void a_named_stream_gets_its_own_arrange_step()
    {
        var ids = streams(feature());

        // Two streams: the scenario's own, then the named one. Distinct, because a name that
        // resolved to the same id would be a fold across one stream pretending to be two — green,
        // and proving nothing the single-stream form did not already prove.
        ids.Length.ShouldBe(2);
        ids[0].ShouldNotBe(ids[1]);
    }

    [Fact]
    public void the_ids_are_derived_from_the_scenario_and_the_name_together()
    {
        // Stable across runs, and the same name in another scenario is another stream — the
        // property that lets a model name streams without minting ids.
        streams(feature()).ShouldBe(streams(feature()));

        var renamed = ModelYaml.Replace("stream: the second appointment", "stream: a different name");
        streams(feature(renamed))[1].ShouldNotBe(streams(feature())[1]);
    }

    [Fact]
    public void the_events_land_under_the_stream_they_name()
    {
        var lines = feature().Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        var own = lines.FindIndex(x => x.StartsWith("Given no events for Appointment"));
        var second = lines.FindIndex(x => x.StartsWith("And no events for Appointment"));
        var events = lines.Select((line, i) => (line, i))
            .Where(x => x.line == "And AppointmentProposed occurred").Select(x => x.i).ToList();

        events.Count.ShouldBe(2);
        events[0].ShouldBeGreaterThan(own);
        events[0].ShouldBeLessThan(second);
        events[1].ShouldBeGreaterThan(second);
    }

    [Fact]
    public void an_act_is_pointed_back_at_the_scenarios_own_stream()
    {
        // The trap this feature would otherwise introduce, and it fails SILENTLY: both are streams
        // of the same aggregate, so an act left pointed at the last named stream simply writes to
        // the wrong one and the scenario's own assertions go quiet rather than red.
        const string withAnAct =
            """
            schema: 1
            model: CritterCrush
            namespace: CritterCrush
            slices:
              - name: ConfirmAppointment
                pattern: Command
                domain: Appointments
                trigger: { kind: MessageHandler }
                command: ConfirmAppointment
                aggregates: [Appointment]
                events: [AppointmentConfirmed]
                specifications:
                  feature: Appointments
                  scenarios:
                    - name: Confirming one of an owner's two appointments
                      given:
                        - { event: AppointmentProposed, with: { ownerId: "0e5e0001-0000-0000-0000-000000000001" } }
                        - event: AppointmentProposed
                          stream: the other appointment
                          with: { ownerId: "0e5e0001-0000-0000-0000-000000000001" }
                      when: { command: ConfirmAppointment, with: { appointmentId: "{streamId}" } }
                      then: [{ event: AppointmentConfirmed }]
            """;

        var lines = feature(withAnAct).Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var ids = streams(feature(withAnAct));

        var act = lines.FindIndex(x => x.StartsWith("When "));
        var arranges = lines.Select((line, i) => (line, i))
            .Where(x => x.line.Contains("no events for Appointment \"")).ToList();

        // The last arrange before the act names the scenario's own stream again.
        var lastBeforeAct = arranges.Last(x => x.i < act);
        lastBeforeAct.line.ShouldContain(ids[0]);
    }

    [Fact]
    public void a_scenario_with_no_named_stream_is_untouched()
    {
        // Additive: an ordinary single-stream scenario pays nothing for this feature. The model is
        // built without the `stream:` key rather than edited out of the one above, so the check
        // cannot pass because a replace silently matched nothing.
        const string single =
            """
            schema: 1
            model: CritterCrush
            namespace: CritterCrush
            slices:
              - name: MyAppointments
                pattern: View
                domain: Appointments
                aggregates: [Appointment]
                projections: [MyAppointmentsProjection]
                readModels: [MyAppointments]
                specifications:
                  feature: Appointments
                  scenarios:
                    - name: An owner sees one appointment
                      given:
                        - { event: AppointmentProposed, with: { ownerId: "0e5e0001-0000-0000-0000-000000000001" } }
                      then:
                        - readModel: MyAppointments
                          id: "0e5e0001-0000-0000-0000-000000000001"
                          contains: { awaitingConfirmation: "1" }
            """;

        single.ShouldNotContain("stream:");

        var text = feature(single);
        streams(text).Length.ShouldBe(1);
        text.ShouldNotContain("And no events for");
    }
}
