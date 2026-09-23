using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #334: a projected INTEGRATION slice can be asked for, so a repo being built gets its
/// skeletons instead of nineteen slices producing one.
/// </summary>
/// <remarks>
/// <para>
/// The filter that produced the gap read the decided table's <c>integration</c> + <c>projected</c>
/// row as a rule: nothing is written, because an existing hand-written suite is adopting the
/// slice. That is right for Marten's DaemonTests and exactly wrong for CritterCrush's Lane A,
/// where the tests do not exist yet — and the value lost is not boilerplate. It is 37 method names
/// that must match the model's scenario names exactly, because a projected test's identity IS its
/// method name.
/// </para>
/// <para>
/// So the manifest says which, and says it out loud in the one corner where both readings are
/// plausible.
/// </para>
/// </remarks>
public class ProjectedIntegrationScaffoldTests
{
    private const string ModelYaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: ConfirmAppointment
            pattern: Command
            domain: Scheduling
            trigger: { kind: Http }
            command: ConfirmAppointment
            aggregates: [Appointment]
            events: [AppointmentConfirmed, AppointmentProposed]
            specifications:
              feature: BookingAppointments
              scenarios:
                - name: A proposed appointment is confirmed
                  given:
                    - { event: AppointmentProposed, with: { appointmentId: "{streamId}" } }
                  when: { command: ConfirmAppointment, with: { appointmentId: "{streamId}" } }
                  then: [{ event: AppointmentConfirmed }]
          - name: CancelAppointment
            pattern: Command
            domain: Scheduling
            trigger: { kind: Http }
            command: CancelAppointment
            aggregates: [Appointment]
            events: [AppointmentCancelled, AppointmentProposed]
            specifications:
              feature: BookingAppointments
              scenarios:
                - name: A proposed appointment is cancelled
                  given:
                    - { event: AppointmentProposed, with: { appointmentId: "{streamId}" } }
                  when: { command: CancelAppointment, with: { appointmentId: "{streamId}" } }
                  then: [{ event: AppointmentCancelled }]
          - name: AppointmentsQueue
            pattern: View
            domain: Scheduling
            readModels: [AppointmentsQueue]
            consumedEvents: [AppointmentProposed]
            specifications:
              scenarios:
                - name: A proposed appointment reaches the queue
                  given:
                    - { event: AppointmentProposed, with: { appointmentId: "{streamId}" } }
                  then: [{ readModel: AppointmentsQueue, contains: { count: "1" } }]
        """;

    private static CuratedModelFile model()
    {
        var reading = CuratedModelReader.Read(ModelYaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private static IReadOnlyDictionary<string, string> scaffold(string manifestYaml)
    {
        var file = model();
        var manifest = SpecOwnershipReader.Read(manifestYaml, file);
        manifest.Problems.ShouldBeEmpty();

        return SliceScaffolder.ScaffoldAll(file, SpecOwnershipPlan.For(manifest.File));
    }

    private const string AllProjected =
        """
        schema: 1
        model: CritterCrush
        defaults:
          kind: integration
          authoring: projected
          scaffold: true
          owner: CritterCrush.Specs.{feature}Specs
        """;

    [Fact]
    public void an_all_projected_repo_scaffolds_a_skeleton_per_feature_from_one_defaults_block()
    {
        var files = scaffold(AllProjected);

        // Two features, two owners, from a manifest that lists no slices at all.
        files.Keys.ShouldContain("Specs/BookingAppointmentsSpecs.cs");
        files.Keys.ShouldContain("Specs/AppointmentsQueueSpecs.cs");

        // And no .feature for any of them: one slice is specified in one place.
        files.Keys.ShouldNotContain(x => x.EndsWith(".feature"));
    }

    [Fact]
    public void the_method_names_are_the_models_scenario_names_exactly()
    {
        // The whole value being lost. A projected test's identity IS its method name, so a
        // hand-typed name that drifts publishes an identity that joins nothing — silently.
        var code = scaffold(AllProjected)["Specs/BookingAppointmentsSpecs.cs"];

        code.ShouldContain("public void a_proposed_appointment_is_confirmed()");
        code.ShouldContain("public void a_proposed_appointment_is_cancelled()");
    }

    [Fact]
    public void the_slice_bindings_are_written_per_method_when_one_class_covers_several()
    {
        var code = scaffold(AllProjected)["Specs/BookingAppointmentsSpecs.cs"];

        code.ShouldContain("[BobcatFeature(\"BookingAppointments\")]");
        code.ShouldContain("[BobcatSlice(SliceType = typeof(ConfirmAppointment))]");
        code.ShouldContain("[BobcatSlice(SliceType = typeof(CancelAppointment))]");
    }

    [Fact]
    public void the_steps_the_model_derives_come_through_as_comments()
    {
        var code = scaffold(AllProjected)["Specs/BookingAppointmentsSpecs.cs"];

        code.ShouldContain("// When ConfirmAppointment is posted to");
        code.ShouldContain("// Then AppointmentConfirmed is emitted");
    }

    [Fact]
    public void an_integration_skeleton_says_it_needs_the_store_rather_than_guessing_a_base_class()
    {
        var code = scaffold(AllProjected)["Specs/BookingAppointmentsSpecs.cs"];

        code.ShouldContain("TODO — these are integration slices: give this class the store");

        // Issue #376's lesson, applied to the other piece of generated prose that names a package:
        // Bobcat.CritterStack stopped being one in 0.27.0, and nothing compiles a TODO comment, so
        // only an assertion keeps it honest.
        code.ShouldContain("no Bobcat.CritterStack package since 0.27.0");
        code.ShouldNotContain("helpers are in Bobcat.CritterStack.");
    }

    [Fact]
    public void a_unit_slice_gets_no_store_todo()
    {
        var files = scaffold(
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: AppointmentsQueue
                kind: unit
                coveredBy: "BookingAppointments/A proposed appointment is confirmed"
                owner: CritterCrush.Specs.QueueSpecs
            """);

        var code = files["Specs/QueueSpecs.cs"];
        code.ShouldNotContain("give this class the store");
        code.ShouldContain("public void a_proposed_appointment_reaches_the_queue()");
    }

    [Fact]
    public void an_adopted_slice_still_scaffolds_nothing()
    {
        // The DaemonTests row, unchanged and now said out loud: the tests predate the model, and
        // generating over them would overwrite a hand-written suite.
        var files = scaffold(
            """
            schema: 1
            model: CritterCrush
            defaults:
              authoring: projected
              scaffold: true
              owner: CritterCrush.Specs.{feature}Specs
            slices:
              - slice: AppointmentsQueue
                scaffold: false
                owner: Existing.Suite.QueueTests
            """);

        files.Keys.ShouldNotContain("Specs/QueueTests.cs");
        files.Keys.ShouldNotContain(x => x.Contains("AppointmentsQueue") && x.EndsWith(".feature"));

        // And the slices that ARE being built are untouched by their neighbour's answer.
        files.Keys.ShouldContain("Specs/BookingAppointmentsSpecs.cs");
    }

    [Fact]
    public void a_slice_the_defaults_put_back_in_the_gherkin_lane_writes_a_feature()
    {
        var files = scaffold(
            """
            schema: 1
            model: CritterCrush
            defaults:
              authoring: projected
              scaffold: true
              owner: CritterCrush.Specs.{feature}Specs
            slices:
              - slice: AppointmentsQueue
                authoring: gherkin
            """);

        files.Keys.ShouldContain(x => x.EndsWith("AppointmentsQueue.feature"));
        files.Keys.ShouldContain("Specs/BookingAppointmentsSpecs.cs");
    }

    [Fact]
    public void a_manifest_with_nothing_in_it_scaffolds_exactly_what_it_always_did()
    {
        // The compatibility claim, asserted rather than assumed: `defaults:` is additive, and a
        // file that declares none leaves every slice a Gherkin integration slice.
        var withHeader = scaffold(
            """
            schema: 1
            model: CritterCrush
            """);

        var without = SliceScaffolder.ScaffoldAll(model());

        withHeader.Keys.OrderBy(x => x).ShouldBe(without.Keys.OrderBy(x => x));
        withHeader.Keys.ShouldContain(x => x.EndsWith("BookingAppointments.feature"));
    }
}
