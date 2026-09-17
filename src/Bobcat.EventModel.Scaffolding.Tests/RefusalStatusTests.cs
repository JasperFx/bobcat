using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #337: a modelled HTTP refusal states the status it answers with, instead of every one of
/// them scaffolding as a 400.
/// </summary>
/// <remarks>
/// <para>
/// The old <c>validationFails:</c> carried a message and nothing else, so <c>RefusalSteps</c>
/// wrote <c>Then the response is 400</c> and <c>CollapsedEndpointFrame</c> wrote a matching
/// <c>Status = 400</c> guard TODO. A slice refusing with a 403, a 409 or a 404 got both halves
/// confidently wrong — the same silent degradation as #318, and the reason CritterCrush's 404
/// specification had an identity the model could not declare.
/// </para>
/// <para>
/// The 404 case is the interesting one, and it is not a guard: Wolverine answers it itself for a
/// non-nullable <c>[WriteModel]</c>, before <c>Validate</c> runs. So declaring one is a claim
/// about the SIGNATURE, and the scaffold owes a required parameter and a comment rather than a
/// TODO nobody can reach.
/// </para>
/// </remarks>
public class RefusalStatusTests
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
            events: [AppointmentConfirmed, AppointmentProposed, AppointmentCancelled]
            specifications:
              feature: BookingAppointments
              scenarios:
                - name: A proposed appointment is confirmed
                  given:
                    - { event: AppointmentProposed, with: { appointmentId: "{streamId}" } }
                  when: { command: ConfirmAppointment, with: { appointmentId: "{streamId}" } }
                  then: [{ event: AppointmentConfirmed }]
                - name: A cancelled appointment cannot be confirmed
                  given:
                    - { event: AppointmentProposed, with: { appointmentId: "{streamId}" } }
                    - { event: AppointmentCancelled, with: { appointmentId: "{streamId}" } }
                  when: { command: ConfirmAppointment, with: { appointmentId: "{streamId}" } }
                  then:
                    - refusedWith: { status: 409, reason: "This appointment was cancelled" }
                - name: An appointment that does not exist is 404
                  when: { command: ConfirmAppointment, with: { appointmentId: "{streamId}" } }
                  then:
                    - refusedWith: { status: 404, reason: "no such appointment" }
        """;

    private static CuratedModelFile model()
    {
        var reading = CuratedModelReader.Read(ModelYaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    /// <summary>The slice's own file — the endpoint, its Validate railway, and its records.</summary>
    private static string scaffolded(CuratedModelFile file)
        => SliceScaffolder.Scaffold(file, file.Slices.Single())["Scheduling/ConfirmAppointment.cs"];

    [Fact]
    public void the_stated_status_reaches_the_feature()
    {
        var feature = SliceScaffolder.ScaffoldFeatures(model()).Single().Value;

        feature.ShouldContain("# refused with: \"This appointment was cancelled\"");
        feature.ShouldContain("Then the response is 409");

        feature.ShouldContain("# refused with: \"no such appointment\"");
        feature.ShouldContain("Then the response is 404");

        // The assumption this replaced, gone from a feature whose refusals are 409 and 404.
        feature.ShouldNotContain("Then the response is 400");
    }

    [Fact]
    public void the_stated_status_reaches_the_guard_stub()
    {
        var code = scaffolded(model());

        code.ShouldContain(
            "// TODO guard: return new ProblemDetails { Detail = \"This appointment was cancelled\", Status = 409 };");
    }

    [Fact]
    public void a_declared_404_makes_the_write_model_required_and_writes_no_guard()
    {
        var code = scaffolded(model());

        // The signature is the behaviour: Wolverine's own not-found guard answers the 404.
        code.ShouldContain("Post(ConfirmAppointment command, [WriteModel] Appointment appointment)");
        code.ShouldContain("Validate(ConfirmAppointment command, Appointment appointment)");
        code.ShouldNotContain("Appointment? appointment");

        // And the dead guard is refused by name, because eleven copies of it shipped once.
        code.ShouldContain("404 (\"no such appointment\") is Wolverine's own guard");
        code.ShouldNotContain("Status = 404");
    }

    [Fact]
    public void a_thrown_refusal_still_means_400_on_the_http_lane()
    {
        // Every curated file written before #337 keeps scaffolding exactly as it did: a
        // validationFails: refusal is the 400 the emitted Validate railway returns.
        var reading = CuratedModelReader.Read(StatefulGuardTests.ModelYaml);
        reading.Problems.ShouldBeEmpty();

        var feature = SliceScaffolder.ScaffoldFeatures(reading.File!)
            .Single(x => x.Key.Contains("Appointments")).Value;

        feature.ShouldContain("Then the response is 400");
    }

    [Fact]
    public void the_refusal_list_is_derived_once_for_both_halves()
    {
        var refusals = SliceScaffolder.RefusalsOf(model().Slices.Single());

        refusals.Select(x => x.Reason).ShouldBe(["This appointment was cancelled", "no such appointment"]);
        refusals[0].HttpStatus.ShouldBe(409);
        refusals[0].FromTheFramework.ShouldBeFalse();
        refusals[1].HttpStatus.ShouldBe(404);
        refusals[1].FromTheFramework.ShouldBeTrue();
    }

    [Fact]
    public void a_404_over_arranged_history_is_the_slices_own_decision()
    {
        // A stream that exists cannot be missing, so THAT 404 is a guard the author writes.
        var reading = CuratedModelReader.Read(
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
                    - name: A purged appointment is gone
                      given:
                        - { event: AppointmentProposed, with: { appointmentId: "{streamId}" } }
                      when: { command: ConfirmAppointment, with: { appointmentId: "{streamId}" } }
                      then:
                        - refusedWith: { status: 404, reason: "this appointment has been purged" }
            """);
        reading.Problems.ShouldBeEmpty();

        var slice = reading.File!.Slices.Single();
        SliceScaffolder.RefusesMissingStream(slice).ShouldBeFalse();
        SliceScaffolder.RefusalsOf(slice).Single().FromTheFramework.ShouldBeFalse();

        var code = scaffolded(reading.File!);

        code.ShouldContain("Status = 404");
        code.ShouldContain("[WriteModel] Appointment? appointment");
    }

    [Fact]
    public void the_shape_predicate_is_shared_so_the_reader_and_the_scaffolder_cannot_disagree()
    {
        // The reader refuses `refusedWith:` off the HTTP lane, and the scaffolder decides the
        // endpoint shape. Two copies of that question is how a format rule and a code shape
        // drift; there is one.
        var file = model();
        var slice = file.Slices.Single();

        CuratedSliceShape.AnswersOverHttp(slice).ShouldBeTrue();
        SliceScaffolder.PlanFor(file, slice).OverHttp.ShouldBeTrue();

        slice.Trigger!.Kind = "Scheduled";
        CuratedSliceShape.AnswersOverHttp(slice).ShouldBeFalse();
        SliceScaffolder.PlanFor(file, slice).OverHttp.ShouldBeFalse();
    }
}
