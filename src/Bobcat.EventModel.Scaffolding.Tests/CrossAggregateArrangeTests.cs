using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #320: a curated <c>given:</c> can name another AGGREGATE, not just another stream.
/// </summary>
/// <remarks>
/// The shipped Gherkin always could — the aggregate is in the step text. The curated format could
/// not, so a rule spanning two aggregates was unsayable: every arranged event landed on the acting
/// slice's own aggregate. Which for a multi-stream projection happens to WORK, because a projection
/// routes by its identity rule and does not care what the stream is typed as — worse than failing,
/// because the model then says something untrue and the specs pass.
///
/// The board's own test for CritterCrush's AcceptHomeCheckAssignment is the motivating case
/// (CritterStackSamples#21): it arranges Home Check Requested AND Volunteer Approved.
/// </remarks>
public class CrossAggregateArrangeTests
{
    private const string Yaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: ApproveVolunteer
            pattern: Command
            domain: Volunteering
            trigger: { kind: Http, label: Approve Volunteer }
            command: ApproveVolunteer
            aggregates: [VolunteerApplication]
            events: [VolunteerApproved]
            elements:
              VolunteerApproved:
                fields: { applicantOwnerId: Guid }
            specifications:
              feature: Volunteering
              scenarios:
                - name: A reviewed applicant is approved
                  when: { command: ApproveVolunteer, with: { applicantOwnerId: "{streamId}" } }
                  then: [{ event: VolunteerApproved }]
          - name: AcceptHomeCheckAssignment
            pattern: Command
            domain: Volunteering
            trigger: { kind: Http, label: Home Check Assigned }
            command: AcceptHomeCheckAssignment
            aggregates: [HomeCheck]
            events: [HomeCheckAssignmentAccepted]
            elements:
              AcceptHomeCheckAssignment:
                fields: { homeCheckId: Guid }
              HomeCheckRequested:
                fields: { ownerId: Guid }
              HomeCheckAssignmentAccepted:
                fields: { ownerId: Guid }
            specifications:
              feature: HomeChecks
              scenarios:
                - name: An approved volunteer accepts an assignment
                  given:
                    - { event: HomeCheckRequested }
                    - { event: VolunteerApproved, aggregate: VolunteerApplication }
                  when: { command: AcceptHomeCheckAssignment, with: { homeCheckId: "{streamId}" } }
                  then: [{ event: HomeCheckAssignmentAccepted }]
        """;

    private static CuratedModelFile model()
    {
        var reading = CuratedModelReader.Read(Yaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private static string[] feature()
    {
        var file = model();
        return SliceScaffolder
            .ScaffoldFeatures(file)
            .Single(x => x.Key.Contains("HomeChecks")).Value
            .Split('\n').Select(x => x.TrimEnd()).ToArray();
    }

    [Fact]
    public void an_arranged_event_on_another_aggregate_re_points_to_it()
    {
        var lines = feature();
        var scenario = Array.FindIndex(lines, x => x.Contains("An approved volunteer accepts"));
        var body = string.Join("\n", lines.Skip(scenario));

        // The HomeCheck arrangement runs on the scenario's own stream; the VolunteerApproved one
        // re-points to a VolunteerApplication stream by name in the step text.
        body.ShouldContain("And HomeCheckRequested occurred");
        body.ShouldContain("And no events for VolunteerApplication \"");
        body.ShouldContain("And VolunteerApproved occurred");

        // …and the act goes back to the scenario's own HomeCheck stream. Leaving it pointed at the
        // other aggregate would run the act against the wrong one, silently.
        var steps = lines.Skip(scenario).Where(x => x.Contains("no events for") || x.Contains("is posted to")).ToArray();
        steps[^2].ShouldContain("no events for HomeCheck");
        steps[^1].ShouldContain("is posted to");
    }

    [Fact]
    public void the_two_streams_get_different_ids()
    {
        var lines = feature();
        var ids = lines.Where(x => x.Contains("no events for"))
            .Select(x => x.Split('"')[1])
            .ToArray();

        // Three steps: the scenario's own stream, the VolunteerApplication one, then back. The
        // first and last are the same stream; the middle must not collide with it.
        ids.Length.ShouldBe(3);
        ids[0].ShouldBe(ids[2]);
        ids[1].ShouldNotBe(ids[0]);
    }

    [Fact]
    public void naming_an_aggregate_perturbs_nothing_that_does_not_name_one()
    {
        // The id key APPENDS the aggregate rather than re-shaping the key, so every model that
        // names no aggregate scaffolds exactly as before and a re-scaffold stays a no-op. Proven
        // by scaffolding the same model with and without the annotation and comparing the feature
        // that carries none — not by asserting a hard-coded id, which would pass or fail for
        // reasons of its own.
        var withAggregate = CuratedModelReader.Read(Yaml).File!;
        var without = CuratedModelReader.Read(Yaml.Replace(", aggregate: VolunteerApplication", "")).File!;

        // The fixture actually differs, or this compares a model with itself.
        withAggregate.Slices.SelectMany(x => x.Specifications!.Scenarios).SelectMany(x => x.Given)
            .ShouldContain(x => x.Aggregate == "VolunteerApplication");
        without.Slices.SelectMany(x => x.Specifications!.Scenarios).SelectMany(x => x.Given)
            .ShouldAllBe(x => x.Aggregate == null);

        string volunteering(CuratedModelFile file) =>
            SliceScaffolder.ScaffoldFeatures(file).Single(x => x.Key.Contains("Volunteering")).Value;

        volunteering(withAggregate).ShouldBe(volunteering(without));
    }

    [Fact]
    public void an_aggregate_no_slice_declares_is_warned_about()
    {
        var reading = CuratedModelReader.Read(Yaml.Replace("aggregate: VolunteerApplication", "aggregate: VolunteerApplicaton"));

        // Still loads — the type may exist in code the model has not caught up with — but a typo
        // would otherwise scaffold an arrange against a stream of a type nothing else mentions.
        reading.Succeeded.ShouldBeTrue();
        var warning = reading.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("VolunteerApplicaton");
        warning.ShouldContain("no slice in this model declares");
    }
}
