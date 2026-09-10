using Bobcat.EventModel;
using Bobcat.EventModel.Scaffolding;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #259: the scaffolder can rewrite history repeated across a feature's scenarios into named
/// <c>@arrangement</c> scenarios — but only when asked, because it changes what a regenerated
/// feature looks like.
/// </summary>
public class HistoryArrangementTests
{
    private static CuratedScenario scenario(string name, params string[] events)
        => new() { Name = name, Given = events.Select(x => new CuratedGiven { Event = x }).ToList() };

    [Fact]
    public void the_chapter_shape_yields_the_three_arrangements_a_person_wrote_by_hand()
    {
        // BookingAppointments in miniature: everyone proposes, some confirm, two of those complete,
        // and a few go their own way after the proposal.
        var scenarios = new[]
        {
            scenario("a", "Proposed"),
            scenario("b", "Proposed"),
            scenario("c", "Proposed", "Cancelled"),
            scenario("d", "Proposed", "Confirmed"),
            scenario("e", "Proposed", "Confirmed"),
            scenario("f", "Proposed", "Confirmed", "Completed"),
            scenario("g", "Proposed", "Confirmed", "Completed"),
        };

        var plan = HistoryArrangements.Plan(scenarios);

        plan.Arrangements.Select(x => x.Name).ShouldBe(
            ["proposed", "proposed, then confirmed", "proposed, then confirmed, then completed"]);

        // Each builds on the one before and adds only its own event.
        plan.Arrangements[1].Parent.ShouldBe(plan.Arrangements[0]);
        plan.Arrangements[2].Events.Select(x => x.Event).ShouldBe(["Completed"]);

        // A scenario references the deepest arrangement on its path, and keeps the rest longhand.
        plan.For(scenarios[2]).Arrangement!.Name.ShouldBe("proposed");
        plan.For(scenarios[2]).Consumed.ShouldBe(1);
        plan.For(scenarios[6]).Arrangement!.Name.ShouldBe("proposed, then confirmed, then completed");
        plan.ScenariosUsing.ShouldBe(7);
    }

    [Fact]
    public void history_nobody_shares_is_left_longhand()
    {
        var plan = HistoryArrangements.Plan([scenario("a", "Proposed"), scenario("b", "Cancelled")]);

        plan.Arrangements.ShouldBeEmpty();
        plan.For(new CuratedScenario()).Arrangement.ShouldBeNull();
    }

    [Fact]
    public void scenarios_that_always_continue_together_get_one_arrangement_not_two()
    {
        // "proposed" alone would be referenced by nobody — every scenario that proposes also confirms.
        var plan = HistoryArrangements.Plan([scenario("a", "Proposed", "Confirmed"), scenario("b", "Proposed", "Confirmed")]);

        var arrangement = plan.Arrangements.ShouldHaveSingleItem();
        arrangement.Events.Select(x => x.Event).ShouldBe(["Proposed", "Confirmed"]);
        arrangement.Parent.ShouldBeNull();
    }

    [Fact]
    public void the_same_event_with_different_values_is_different_history()
    {
        var first = new CuratedScenario
        {
            Name = "a", Given = [new CuratedGiven { Event = "Proposed", With = new() { ["ownerId"] = "o1" } }]
        };
        var second = new CuratedScenario
        {
            Name = "b", Given = [new CuratedGiven { Event = "Proposed", With = new() { ["ownerId"] = "o2" } }]
        };

        HistoryArrangements.Plan([first, second]).Arrangements.ShouldBeEmpty();
    }

    [Fact]
    public void an_event_carrying_the_stream_id_is_never_shared()
    {
        // {streamId} expands to a different id in every scenario, so an arrangement holding it would
        // hand one scenario's stream id to another's history.
        CuratedScenario withStreamId(string name) => new()
        {
            Name = name,
            Given = [new CuratedGiven { Event = "Proposed", With = new() { ["appointmentId"] = SliceScaffolder.StreamIdToken } }]
        };

        HistoryArrangements.Plan([withStreamId("a"), withStreamId("b")]).Arrangements.ShouldBeEmpty();
    }

    [Fact]
    public void event_names_read_as_words()
    {
        HistoryArrangements.NameFor("HomeCheckAppointmentProposed").ShouldBe("home check appointment proposed");
        HistoryArrangements.NameFor("HTTPRequestSent").ShouldBe("http request sent");
    }

    private const string ModelYaml =
        """
        schema: 1
        model: Shelter
        namespace: Shelter
        slices:
          - name: ConfirmAppointment
            pattern: Command
            trigger: { kind: Http, label: My appointments }
            command: ConfirmAppointment
            aggregates: [Appointment]
            events: [AppointmentConfirmed]
            specifications:
              feature: Appointments
              scenarios:
                - name: A proposed appointment is confirmed
                  given: [{ event: AppointmentProposed, with: { ownerId: "o1" } }]
                  when: { command: ConfirmAppointment }
                  then: [{ event: AppointmentConfirmed }]
                - name: A cancelled appointment cannot be confirmed
                  given:
                    - { event: AppointmentProposed, with: { ownerId: "o1" } }
                    - { event: AppointmentCancelled }
                  when: { command: ConfirmAppointment }
                  then: [{ event: AppointmentConfirmed }]
        """;

    private static CuratedModelFile model()
    {
        var reading = CuratedModelReader.Read(ModelYaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private static string[] linesOf(string feature) => feature.Split('\n').Select(x => x.TrimEnd('\r')).ToArray();

    [Fact]
    public void the_scaffolder_writes_arrangements_only_when_asked()
    {
        var longhand = SliceScaffolder.ScaffoldFeatures(model()).Single().Value;
        longhand.ShouldNotContain("@arrangement");
        longhand.ShouldNotContain("the arrangement");

        var lines = linesOf(SliceScaffolder.ScaffoldFeatures(model(), arrangements: true).Single().Value);

        lines.ShouldContain("  @arrangement");
        lines.ShouldContain("  Scenario: appointment proposed");
        lines.Count(x => x == "    And the arrangement \"appointment proposed\"").ShouldBe(2);

        // The rest of a scenario's history stays longhand after the reference.
        lines.ShouldContain("    And AppointmentCancelled occurred");

        // Written once, in the arrangement — not restated in each scenario.
        lines.Count(x => x.Contains("AppointmentProposed occurred")).ShouldBe(1);
    }

    [Fact]
    public void repeated_history_is_reported_so_a_caller_can_ask_first()
    {
        var repeated = SliceScaffolder.FindRepeatedHistory(model()).ShouldHaveSingleItem();

        repeated.Feature.ShouldBe("Appointments");
        repeated.Arrangements.ShouldBe(["appointment proposed"]);
        repeated.Scenarios.ShouldBe(2);
    }
}
