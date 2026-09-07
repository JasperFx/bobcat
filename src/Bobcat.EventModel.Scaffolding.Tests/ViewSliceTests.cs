using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #240: a View slice is scaffolded from what the model actually says — its element hints
/// become the read model's columns, and its arrange steps name the stream the projection reads
/// rather than a write model that does not exist.
/// </summary>
/// <remarks>
/// <c>ViewSliceFrame</c> was the one frame that never called <c>fieldsFor</c>, so the read model —
/// the one type a curated model has the most to say about — came out holding an <c>Id</c> and a
/// comment pointing at scenarios whose columns were also being ignored. And a View slice declares
/// no <c>aggregates:</c>, so <c>AggregateFor</c> synthesized <c>{Slice}Model</c> for its arrange
/// step: a name nothing emits, which is BOBCAT011 and takes the whole spec project down.
/// </remarks>
public class ViewSliceTests
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
            events: [HomeCheckAppointmentProposed]
          - name: AppointmentsQueue
            pattern: View
            domain: Appointments
            projections: [AppointmentsQueueProjection]
            readModels: [AppointmentsQueue]
            elements:
              AppointmentsQueue:
                fields: { ownerId: Guid, shelterId: Guid, kind: string, scheduledFor: DateTimeOffset }
            specifications:
              feature: Appointments
              scenarios:
                - name: A proposed appointment joins the queue
                  given:
                    - { event: HomeCheckAppointmentProposed, with: { ownerId: "{streamId}" } }
                  then:
                    - readModel: AppointmentsQueue
                      contains: { status: proposed, awaitingAction: "true" }
        """;

    private static IReadOnlyDictionary<string, string> scaffold(string yaml = ModelYaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();
        return SliceScaffolder.ScaffoldAll(reading.File!);
    }

    [Fact]
    public void the_read_model_carries_the_columns_the_model_names()
    {
        var code = scaffold()["Appointments/AppointmentsQueue.cs"];

        // From `elements:` …
        code.ShouldContain("public Guid OwnerId { get; set; }");
        code.ShouldContain("public Guid ShelterId { get; set; }");
        code.ShouldContain("public string Kind { get; set; }");
        code.ShouldContain("public DateTimeOffset ScheduledFor { get; set; }");

        // … and from the columns the scenarios assert on, which the old TODO pointed at while
        // reading neither.
        code.ShouldContain("public string Status { get; set; }");
        code.ShouldContain("public bool AwaitingAction { get; set; }");

        code.ShouldNotContain("// TODO: the projected columns");
    }

    [Fact]
    public void the_arrange_step_names_the_stream_the_projection_reads()
    {
        var feature = scaffold()["Features/Appointments.feature"];

        // The events arranged are declared by a sibling slice whose `aggregates:` say Appointment.
        feature.ShouldContain("Given no events for Appointment ");
        feature.ShouldContain("And events for Appointment");
        feature.ShouldNotContain("AppointmentsQueueModel");
    }

    [Fact]
    public void arranged_events_spanning_several_aggregates_are_warned_about_rather_than_picked_silently()
    {
        // A second declaring slice puts the same event on a different aggregate. Choosing in
        // silence is how a scaffold comes to arrange a stream the scenario never meant.
        var yaml = ModelYaml.Replace(
            "  - name: AppointmentsQueue",
            """
              - name: RecordProposalAudit
                pattern: Command
                domain: Audit
                trigger: { kind: MessageHandler }
                aggregates: [ProposalAudit]
                events: [HomeCheckAppointmentProposed]
              - name: AppointmentsQueue
            """.TrimEnd());

        var feature = scaffold(yaml)["Features/Appointments.feature"];

        feature.ShouldContain("# WARNING: the events this slice arranges belong to several aggregates");
        feature.ShouldContain("Given no events for Appointment ");
    }

    [Fact]
    public void a_view_slice_whose_arranged_events_belong_to_nobody_omits_the_arrange_steps()
    {
        // There is no honest type to name, and naming one anyway is the BOBCAT011 this issue is.
        var yaml = ModelYaml.Replace("aggregates: [Appointment]", "aggregates: []");

        var feature = scaffold(yaml)["Features/Appointments.feature"];

        feature.ShouldContain("# WARNING: no slice in this model declares an aggregate for the events arranged below");
        feature.ShouldNotContain("Given no events for");
        feature.ShouldNotContain("AppointmentsQueueModel");
    }

    [Fact]
    public void a_command_slice_still_gets_its_synthesized_write_model_name()
    {
        // The fix must not overreach: a Command or Automation handler binds a [WriteModel] of the
        // synthesized name, and ScaffoldAggregates emits a type for it.
        var model = CuratedModelReader.Read(ModelYaml).File!;
        var queue = model.Slices.Single(x => x.Name == "AppointmentsQueue");

        SliceScaffolder.ArrangeAggregateFor(model, queue).ShouldBe("Appointment");
        SliceScaffolder.AggregateFor(queue).ShouldBe("AppointmentsQueueModel");
    }
}
