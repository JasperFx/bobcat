using Bobcat.Monitor.Coordination;
using Bobcat.Monitor.Coordination.GitHub;
using Bobcat.Monitor.Coordination.NuGet;
using Shouldly;

namespace Bobcat.Monitor.Tests;

public class PlanStatusTests
{
    private static readonly NuGetStatusCache emptyNuGet = new();
    private static readonly NuGetBaselineStore baselines =
        new(Path.Combine(Path.GetTempPath(), "bobcat-plan-status-tests", Guid.NewGuid().ToString("N")));

    private static PlanStatusView statusFor(RegisteredPlan plan, GitHubStatusCache cache)
        => PlanStatus.For(plan, cache, emptyNuGet, baselines);

    private static RegisteredPlan plan(string yaml)
    {
        var result = PlanParser.Parse(yaml);
        result.Errors.ShouldBeEmpty();
        return new RegisteredPlan("test", PlanSource.Pushed, result.Document, null, DateTimeOffset.UtcNow, []);
    }

    private static GitHubObservation issue(
        string @ref, string state = "open", string[]? labels = null, string[]? assignees = null, ClosingPr[]? closing = null)
        => new(@ref, "issue", state, "observed title", labels ?? [], assignees ?? [], closing ?? [], false, DateTimeOffset.UtcNow);

    private const string Chain =
        """
        schema: 1
        plan: chain
        repos:
          bobcat: JasperFx/bobcat
        nodes:
          - id: first
            kind: issue
            repo: bobcat
            issue: 1
          - id: ship
            kind: publish
            package: X
            bump: minor
            depends_on: [first]
          - id: second
            kind: issue
            repo: bobcat
            issue: 2
            depends_on: [ship]
        """;

    [Fact]
    public void statuses_derive_from_observation_and_ready_from_the_dag()
    {
        var cache = new GitHubStatusCache();
        cache.Upsert(issue("JasperFx/bobcat#1", state: "closed"));
        cache.Upsert(issue("JasperFx/bobcat#2"));

        var view = statusFor(plan(Chain), cache);

        var byId = view.Nodes.ToDictionary(x => x.Id);
        byId["first"].Status.ShouldBe("done");
        byId["first"].Ready.ShouldBeFalse(); // done work is never "ready"

        byId["ship"].Status.ShouldBe("unknown"); // its feed hasn't been observed in this test
        byId["ship"].Ready.ShouldBeTrue(); // its one dependency is done

        byId["second"].Status.ShouldBe("open");
        byId["second"].Ready.ShouldBeFalse(); // blocked behind the publish

        view.Ready.ShouldBe(["ship"]);
    }

    [Fact]
    public void the_claim_signals_are_the_label_or_an_assignee()
    {
        var cache = new GitHubStatusCache();
        cache.Upsert(issue("JasperFx/bobcat#1", labels: [PlanStatus.ClaimedLabel]));
        cache.Upsert(issue("JasperFx/bobcat#2", assignees: ["somebody"]));

        var view = statusFor(plan(Chain), cache);

        view.Nodes.Single(x => x.Id == "first").Status.ShouldBe("claimed");
        view.Nodes.Single(x => x.Id == "second").Status.ShouldBe("claimed");
        view.Nodes.Single(x => x.Id == "second").Assignees.ShouldBe(["somebody"]);
    }

    [Fact]
    public void an_open_closing_pr_beats_the_claim_signals()
    {
        var cache = new GitHubStatusCache();
        cache.Upsert(issue("JasperFx/bobcat#1",
            labels: [PlanStatus.ClaimedLabel],
            closing: [new ClosingPr(95, "open", false)]));

        var view = statusFor(plan(Chain), cache);

        var first = view.Nodes.Single(x => x.Id == "first");
        first.Status.ShouldBe("pr-open");
        first.OpenPrs.ShouldBe([95]);
    }

    [Fact]
    public void unbound_unobserved_and_missing_are_three_different_truths()
    {
        var yaml =
            """
            schema: 1
            plan: truths
            repos:
              bobcat: JasperFx/bobcat
            nodes:
              - id: unbound
                kind: issue
                repo: bobcat
              - id: unobserved
                kind: issue
                repo: bobcat
                issue: 7
              - id: gone
                kind: issue
                repo: bobcat
                issue: 8
            """;

        var cache = new GitHubStatusCache();
        cache.Upsert(issue("JasperFx/bobcat#8", state: "missing"));

        var view = statusFor(plan(yaml), cache);

        var byId = view.Nodes.ToDictionary(x => x.Id);
        byId["unbound"].Status.ShouldBe("unrealized"); // no number yet — ready means "create it"
        byId["unbound"].Ready.ShouldBeTrue();
        byId["unobserved"].Status.ShouldBe("unknown"); // bound, GitHub not heard from yet
        byId["gone"].Status.ShouldBe("missing"); // GitHub answered: nothing there
        byId["gone"].Ready.ShouldBeFalse(); // not workable — the plan needs fixing, not an agent
    }

    [Fact]
    public void a_merged_pr_node_is_done_but_a_closed_one_is_abandoned_and_still_blocks()
    {
        var yaml =
            """
            schema: 1
            plan: prs
            repos:
              bobcat: JasperFx/bobcat
            nodes:
              - id: merged-pr
                kind: pr
                repo: bobcat
                pr: 10
              - id: dead-pr
                kind: pr
                repo: bobcat
                pr: 11
              - id: downstream
                kind: issue
                repo: bobcat
                issue: 12
                depends_on: [merged-pr, dead-pr]
            """;

        var cache = new GitHubStatusCache();
        cache.Upsert(new GitHubObservation("JasperFx/bobcat#10", "pr", "merged", "t", [], [], [], false, DateTimeOffset.UtcNow));
        cache.Upsert(new GitHubObservation("JasperFx/bobcat#11", "pr", "closed", "t", [], [], [], false, DateTimeOffset.UtcNow));
        cache.Upsert(issue("JasperFx/bobcat#12"));

        var view = statusFor(plan(yaml), cache);

        var byId = view.Nodes.ToDictionary(x => x.Id);
        byId["merged-pr"].Status.ShouldBe("done");
        byId["dead-pr"].Status.ShouldBe("abandoned");
        byId["downstream"].Ready.ShouldBeFalse(); // abandoned is not done — it still blocks
    }
}
