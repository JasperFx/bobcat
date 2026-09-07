using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// The issue #226 acceptance fixture: <b>a scaffold always compiles</b>.
/// </summary>
/// <remarks>
/// The first agent-driven run over a scaffolded chapter — eleven slices, nine agents — produced
/// nine identical reports and zero provable slices, because the scaffolder emitted its judgment
/// points as holes in expression position (<c>new AppointmentConfirmed(/* TODO */)</c>). One
/// unfilled slice fails the whole application project, so no slice's specs can build or run until
/// every slice is filled: the per-slice independence that slice nodes, claims and spec-identity
/// gates all assume does not exist at the source, and the failure reads as "your slice is broken"
/// when the cause is a sibling nobody has touched.
///
/// So the hole moved into the behaviour. Every slice's specs now run on day one and fail on their
/// own assertions — the correct spec-first state — and the failure names the slice that owns it.
/// </remarks>
public class ScaffoldCompilesTests
{
    internal const string ModelYaml =
        """
        schema: 1
        model: Clinic
        namespace: Clinic
        slices:
          # Events carrying real fields: exactly the shape that made a `/* TODO */` argument a
          # compile error rather than a harmless marker.
          - name: ConfirmAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: Http }
            command: ConfirmAppointment
            aggregates: [Appointment]
            events: [AppointmentConfirmed]
            elements:
              AppointmentConfirmed:
                fields: { appointmentId: Guid, confirmedAt: DateTimeOffset }
              ConfirmAppointment:
                fields: { appointmentId: Guid }
            specifications:
              feature: Appointments
              scenarios:
                - name: A proposed appointment is confirmed
                  when: { command: ConfirmAppointment }
                  then: [{ event: AppointmentConfirmed }]
          - name: NotifyOwner
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Appointment Confirmed }
            aggregates: [Appointment]
            events: [OwnerNotified]
            elements:
              OwnerNotified:
                fields: { ownerId: Guid, channel: string }
          # No `aggregates:` — the endpoint still binds a [WriteModel], so the type has to exist.
          - name: RecordNoShow
            pattern: Command
            domain: Appointments
            trigger: { kind: Http }
            command: RecordNoShow
            events: [NoShowRecorded]
            elements:
              NoShowRecorded:
                fields: { appointmentId: Guid }
            specifications:
              feature: Appointments
              scenarios:
                - name: A missed appointment is recorded
                  when: { command: RecordNoShow }
                  then: [{ event: NoShowRecorded }]
          # No `command:` either — the handler's parameter type still needs declaring.
          - name: CancelAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: MessageHandler }
            aggregates: [Appointment]
            events: [AppointmentCancelled]
        """;

    private static CuratedModelFile parse(string yaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private static IEnumerable<(string Path, string Code)> csharpFor(string yaml)
    {
        var model = parse(yaml);

        foreach (var pair in model.Slices.SelectMany(slice => SliceScaffolder.Scaffold(model, slice))
                     .Concat(SliceScaffolder.ScaffoldAggregates(model)))
        {
            yield return (pair.Key, pair.Value);
        }
    }

    private static string scaffold(string sliceName)
    {
        var model = parse(ModelYaml);
        var slice = model.Slices.Single(x => x.Name == sliceName);
        return SliceScaffolder.Scaffold(model, slice).Single().Value;
    }

    /// <summary>
    /// The load-bearing assertion of this type, and it runs over every fixture in the suite
    /// rather than only this one: the invariant is a property of the <see cref="ScaffoldFrame"/>
    /// family, not of any single model, so a frame that reintroduces a hole must trip here
    /// whichever model happens to exercise it.
    /// </summary>
    [Fact]
    public void no_generated_line_leaves_a_comment_standing_where_an_expression_must_be()
    {
        foreach (var yaml in new[] { ModelYaml, SliceScaffolderTests.ModelYaml, BusVisibilityTests.ModelYaml })
        foreach (var (path, code) in csharpFor(yaml))
        foreach (var line in code.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//")) continue;

            line.Contains("/*").ShouldBeFalse(
                $"{path} leaves an inline comment where an expression must be — that does not compile, " +
                $"and one unfilled slice failing the project blocks every other slice's specs (issue #226):{Environment.NewLine}{line}");
        }
    }

    [Fact]
    public void the_unfilled_decision_throws_and_names_the_slice_that_owns_it()
    {
        var code = scaffold("ConfirmAppointment");

        // What an agent reads when its own slice is the unfilled one — rather than a compile
        // error in a file belonging to a slice it was never handed.
        code.ShouldContain(
            "throw new NotImplementedException(\"TODO: ConfirmAppointment — decide which events this slice appends, and what to answer with\");");

        // The deterministic 80% still travels: the shape it replaced is right above it.
        code.ShouldContain(
            "//     return (new ConfirmAppointmentResponse(/* … */), [new AppointmentConfirmed(/* … */)]);");
    }

    [Fact]
    public void an_automation_throws_too_rather_than_appending_a_default_filled_event()
    {
        var code = scaffold("NotifyOwner");

        // Emitting `new OwnerNotified(default, default)` would compile — and would let a lax
        // scenario go green over a handler nobody has written. A scaffold is red until a human
        // decides; that is the whole point of spec-first.
        code.ShouldContain("throw new NotImplementedException(\"TODO: NotifyOwner — decide which events this slice appends\");");
        code.ShouldNotContain("default,");
    }

    [Fact]
    public void a_slice_declaring_no_aggregate_gets_the_write_model_its_endpoint_binds()
    {
        // The other dangling-type defect in the same family: the frames synthesized
        // `{Slice}Model` for a [WriteModel] parameter and nothing ever emitted that type.
        scaffold("RecordNoShow").ShouldContain("[WriteModel] RecordNoShowModel? recordNoShowModel");

        var aggregates = SliceScaffolder.ScaffoldAggregates(parse(ModelYaml));
        aggregates.Keys.ShouldContain("Appointments/RecordNoShowModel.cs");
        aggregates["Appointments/RecordNoShowModel.cs"].ShouldContain("public class RecordNoShowModel");
    }

    [Fact]
    public void a_command_slice_with_no_command_declared_still_declares_the_type_its_handler_takes()
    {
        var code = scaffold("CancelAppointment");

        code.ShouldContain("public record CancelAppointment(");
        code.ShouldContain("public static EventsToAppend Handle(CancelAppointment command,");
    }

    [Fact]
    public void a_feature_file_never_names_a_type_called_TODO()
    {
        // The .feature half of the same defect: a slice with no aggregate wrote
        // `Given no events for TODO "…"`, which is BOBCAT011 — an unresolved type capture that
        // fails the spec project just as thoroughly as a hole fails the app project.
        var feature = SliceScaffolder.ScaffoldFeatures(parse(ModelYaml)).Single().Value;

        feature.ShouldNotContain("for TODO ");
        feature.ShouldContain("Given no events for RecordNoShowModel ");
        feature.ShouldContain("Given no events for Appointment ");
    }
}
