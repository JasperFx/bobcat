using System.Text.RegularExpressions;
using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// The issue #231 acceptance fixture: the two halves of the scaffolder describe the same code.
/// </summary>
/// <remarks>
/// Regenerating the CritterCrush chapter with 0.12.0, the app project compiled (#226) and then
/// the spec project failed <c>BOBCAT011</c> on every command slice. The code generator emitted a
/// collapsed endpoint — deliberately no bus-visible command type anywhere — while the feature
/// generator wrote <c>When ConfirmAppointment is received</c>, a bus dispatch requiring exactly
/// that type; and for automations it named the slice's command where the handler takes the
/// trigger event. Both are build errors, so two disagreeing halves took the whole spec project
/// down: the "one hole fails everything" property #226 had just removed from the app project,
/// reintroduced on the spec side.
///
/// Both halves read one <see cref="SlicePlan"/> now. The last test here is the general form of
/// that, and is the one that would have caught this: every type a scaffolded feature names is a
/// type some scaffolded file declares, and every route it posts to is a route some scaffolded
/// endpoint answers on.
/// </remarks>
public partial class SpecAndCodeAgreementTests
{
    internal const string ModelYaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          # The collapsed default: the endpoint IS the handler, and there is no bus-visible
          # ConfirmAppointment type for a `is received` step to name.
          - name: ConfirmAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: Http, label: Appointment detail }
            command: ConfirmAppointment
            aggregates: [Appointment]
            events: [AppointmentConfirmed]
            elements:
              ConfirmAppointment:
                fields: { appointmentId: Guid }
            specifications:
              feature: Appointments
              scenarios:
                - name: A proposed appointment is confirmed
                  when: { command: ConfirmAppointment, with: { appointmentId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" } }
                  then: [{ event: AppointmentConfirmed }]
                - name: Confirming twice is refused
                  when: { command: ConfirmAppointment }
                  then: [{ validationFails: "already confirmed" }]
          # An automation: the handler takes the TRIGGER event, never the slice's command.
          - name: ProposeHomeCheckAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Home check assignment accepted }
            command: ProposeHomeCheckAppointment
            aggregates: [Appointment]
            events: [HomeCheckAppointmentProposed]
            externalSystems:
              - { name: Volunteering, direction: Inbound }
            specifications:
              feature: Appointments
              scenarios:
                - name: An accepted assignment proposes an appointment
                  when: { command: ProposeHomeCheckAppointment }
                  then: [{ event: HomeCheckAppointmentProposed }]
          # A command genuinely taken off the bus: `is received` was right all along here.
          - name: ReviewAppointment
            pattern: Command
            domain: Moderation
            trigger: { kind: MessageHandler }
            command: ReviewAppointment
            aggregates: [Review]
            events: [AppointmentReviewed]
            specifications:
              feature: Moderation
              scenarios:
                - name: A flagged appointment is reviewed
                  when: { command: ReviewAppointment }
                  then: [{ event: AppointmentReviewed }]
        """;

    private static CuratedModelFile parse(string yaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private static string featureFor(string name)
        => SliceScaffolder.ScaffoldAll(parse(ModelYaml))[$"Features/{name}.feature"];

    private static string codeFor(string sliceName)
    {
        var model = parse(ModelYaml);
        return SliceScaffolder.Scaffold(model, model.Slices.Single(x => x.Name == sliceName)).Single().Value;
    }

    [Fact]
    public void a_collapsed_slice_posts_to_the_route_the_code_generator_emitted()
    {
        var feature = featureFor("Appointments");
        var code = codeFor("ConfirmAppointment");

        // The step HttpGrammars binds (#210), naming the request record the endpoint takes and
        // the route it answers on — both from the same plan that emitted them below.
        feature.ShouldContain("When ConfirmAppointmentRequest is posted to \"/api/appointments/confirmappointment\"");
        code.ShouldContain("[WolverinePost(\"/api/appointments/confirmappointment\")]");
        code.ShouldContain("Post(ConfirmAppointmentRequest request");

        // And NOT the bus dispatch, which requires a type the collapsed shape deliberately omits.
        feature.ShouldNotContain("When ConfirmAppointment is received");
        code.ShouldNotContain("ConfirmAppointmentHandler");
    }

    [Fact]
    public void an_automation_names_the_trigger_event_its_handler_actually_takes()
    {
        var feature = featureFor("Appointments");

        feature.ShouldContain("When HomeCheckAssignmentAccepted is received");
        feature.ShouldNotContain("When ProposeHomeCheckAppointment is received");

        codeFor("ProposeHomeCheckAppointment")
            .ShouldContain("Handle(HomeCheckAssignmentAccepted trigger, [WriteModel] Appointment appointment)");
    }

    [Fact]
    public void a_command_taken_off_the_bus_still_dispatches_by_its_own_name()
    {
        // The case `is received` was always right for — the fix must not overreach into it.
        featureFor("Moderation").ShouldContain("When ReviewAppointment is received");
        codeFor("ReviewAppointment").ShouldContain("Handle(ReviewAppointment command,");
    }

    [Fact]
    public void an_http_refusal_asserts_the_status_because_an_endpoint_does_not_throw()
    {
        var feature = featureFor("Appointments");

        // `Then validation fails with …` has caught-exception semantics, and the Validate stub
        // the code generator emitted returns ProblemDetails with a 400 instead. Emitting the
        // wrong one is not a compile error — it is a permanently red scenario, which is the same
        // disagreement with a quieter bill.
        feature.ShouldContain("# refused with: \"already confirmed\"");
        feature.ShouldContain("Then the response is 400");
        feature.ShouldNotContain("Then validation fails with");
        feature.ShouldContain("And no events are emitted");

        codeFor("ConfirmAppointment").ShouldContain("Status = 400");
    }

    [Fact]
    public void the_feature_names_the_fixture_that_binds_its_steps()
    {
        // Which fixture carries which vocabulary is decided by the same plan that chose the
        // shape, so it is stated rather than left to be discovered as an unbound step.
        featureFor("Appointments").ShouldContain("derive from CritterStackHttpFixture");
        featureFor("Moderation").ShouldContain("derive from CritterStackFixture");
    }

    /// <summary>
    /// The general form, and the assertion that would have caught #231: walk every scaffolded
    /// <c>.feature</c>, pull the type out of every step that captures one, and require that some
    /// scaffolded <c>.cs</c> declares it. That is what <c>BOBCAT011</c> checks, from the outside.
    /// Run over every fixture in the suite, because the invariant belongs to the scaffolder and
    /// not to any one model.
    /// </summary>
    [Fact]
    public void every_type_a_scaffolded_feature_names_is_one_a_scaffolded_file_declares()
    {
        foreach (var yaml in new[]
                 {
                     ModelYaml, SliceScaffolderTests.ModelYaml, BusVisibilityTests.ModelYaml,
                     ScaffoldCompilesTests.ModelYaml, TriggerOriginTests.ModelYaml,
                     ScenarioStreamIdTests.ModelYaml, ReadModelIdentityTests.ModelYaml,
                     StatefulGuardTests.ModelYaml
                 })
        {
            var files = SliceScaffolder.ScaffoldAll(parse(yaml));
            var declared = declaredTypes(files);
            var routes = files.Where(x => x.Key.EndsWith(".cs"))
                .SelectMany(x => RoutePattern().Matches(x.Value).Select(m => m.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (path, feature) in files.Where(x => x.Key.EndsWith(".feature")))
            foreach (var line in feature.Split('\n').Select(x => x.Trim()))
            {
                if (line.StartsWith("#")) continue;

                if (CapturePattern().Match(line) is { Success: true } capture)
                {
                    declared.ShouldContain(capture.Groups[1].Value,
                        $"{path} names a type no scaffolded file declares — that is BOBCAT011, and it fails the whole spec project:{Environment.NewLine}{line}");
                }

                if (PostPattern().Match(line) is { Success: true } post)
                {
                    routes.ShouldContain(post.Groups[1].Value,
                        $"{path} posts to a route no scaffolded endpoint answers on:{Environment.NewLine}{line}");
                }
            }
        }
    }

    private static HashSet<string> declaredTypes(IReadOnlyDictionary<string, string> files)
        => files.Where(x => x.Key.EndsWith(".cs"))
            .SelectMany(x => x.Value.Split('\n'))
            .Select(x => x.Trim())
            .Select(line =>
                line.StartsWith("public record ") ? line["public record ".Length..].Split('(')[0].Split(';')[0] :
                line.StartsWith("public class ") ? line["public class ".Length..].Split(' ')[0].Split(':')[0] :
                null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every shipped step that binds a type-name capture, which is every BOBCAT011 risk.</summary>
    [GeneratedRegex(@"^(?:Given|When|Then|And) (?:no events for |events for |the )?([A-Z]\w*)(?: is | read model| ""|$)")]
    private static partial Regex CapturePattern();

    [GeneratedRegex(@"is posted to ""([^""]+)""")]
    private static partial Regex PostPattern();

    [GeneratedRegex(@"\[Wolverine(?:Post|Get)\(""([^""]+)""\)\]")]
    private static partial Regex RoutePattern();
}
