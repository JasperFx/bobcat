using Bobcat.Monitor.Coordination;
using Bobcat.Monitor.Coordination.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Bobcat.Monitor.Tests;

public class GitHubGraphTests
{
    [Fact]
    public void the_query_covers_every_number_with_both_fragments()
    {
        var query = GitHubGraph.BuildQuery("JasperFx", "bobcat", [90, 91]);

        query.ShouldContain("repository(owner: \"JasperFx\", name: \"bobcat\")");
        query.ShouldContain("i90: issueOrPullRequest(number: 90)");
        query.ShouldContain("i91: issueOrPullRequest(number: 91)");
        query.ShouldContain("closedByPullRequestsReferences");
        query.ShouldContain("fragment PrBits on PullRequest");
    }

    private const string Response =
        """
        {
          "data": {
            "repository": {
              "i90": {
                "__typename": "Issue",
                "state": "OPEN",
                "title": "Plan document schema",
                "assignees": { "nodes": [{ "login": "somebody" }] },
                "labels": { "nodes": [{ "name": "agent:working" }] },
                "closedByPullRequestsReferences": { "nodes": [{ "number": 95, "state": "OPEN", "merged": false }] }
              },
              "i91": {
                "__typename": "PullRequest",
                "state": "MERGED",
                "title": "The PR",
                "isDraft": false,
                "merged": true,
                "labels": { "nodes": [] }
              },
              "i92": null
            }
          }
        }
        """;

    [Fact]
    public void parses_issues_prs_and_missing_references()
    {
        var at = DateTimeOffset.UtcNow;
        var observations = GitHubGraph.ParseResponse("JasperFx", "bobcat", Response, at);

        observations.Count.ShouldBe(3);

        var issue = observations.Single(x => x.Ref == "JasperFx/bobcat#90");
        issue.Kind.ShouldBe("issue");
        issue.State.ShouldBe("open");
        issue.Assignees.ShouldBe(["somebody"]);
        issue.Labels.ShouldBe(["agent:working"]);
        issue.ClosingPrs.ShouldBe([new ClosingPr(95, "open", false)]);

        var pr = observations.Single(x => x.Ref == "JasperFx/bobcat#91");
        pr.Kind.ShouldBe("pr");
        pr.State.ShouldBe("merged");

        // The null alias — a reference GitHub says points at nothing.
        observations.Single(x => x.Ref == "JasperFx/bobcat#92").State.ShouldBe("missing");
    }

    [Fact]
    public void a_refusal_surfaces_githubs_own_errors()
    {
        var refusal = """{ "data": null, "errors": [{ "message": "Bad credentials" }] }""";

        var e = Should.Throw<InvalidOperationException>(
            () => GitHubGraph.ParseResponse("JasperFx", "bobcat", refusal, DateTimeOffset.UtcNow));
        e.Message.ShouldContain("Bad credentials");
    }
}

public class GitHubStatusCacheTests
{
    private static GitHubObservation observation(string state = "open", string[]? labels = null)
        => new("o/r#1", "issue", state, "t", labels ?? [], [], [], false, DateTimeOffset.UtcNow);

    [Fact]
    public void upsert_reports_change_only_when_something_beyond_the_timestamp_moved()
    {
        var cache = new GitHubStatusCache();

        cache.Upsert(observation()).ShouldBeTrue(); // first sighting
        cache.Upsert(observation()).ShouldBeFalse(); // same state, newer timestamp
        cache.Upsert(observation(state: "closed")).ShouldBeTrue();
        cache.Upsert(observation(state: "closed", labels: ["agent:working"])).ShouldBeTrue();
    }
}

public class GitHubPollerTests : IDisposable
{
    private readonly string _plansPath =
        Path.Combine(Path.GetTempPath(), "bobcat-github-poller-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_plansPath, recursive: true); }
        catch { }
    }

    private PlanRegistry registryWith(string yaml)
    {
        Directory.CreateDirectory(_plansPath);
        File.WriteAllText(Path.Combine(_plansPath, "plan.yaml"), yaml);
        return new PlanRegistry(_plansPath);
    }

    private sealed class FakeClient : IGitHubQueryClient
    {
        public List<string> Queries { get; } = [];
        public required Func<string, string> Respond { get; init; }

        public Task<string> PostQueryAsync(string query, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(Respond(query));
        }
    }

    [Fact]
    public void collects_only_bound_issue_and_pr_references_grouped_by_repo()
    {
        var registry = registryWith(
            """
            schema: 1
            plan: refs
            repos:
              bobcat: JasperFx/bobcat
            nodes:
              - id: bound
                kind: issue
                repo: bobcat
                issue: 90
              - id: unbound
                kind: issue
                repo: bobcat
              - id: elsewhere
                kind: issue
                repo: JasperFx/jasperfx
                issue: 486
              - id: ship
                kind: publish
                package: X
                bump: minor
            """);

        var references = GitHubPoller.CollectReferences(registry.All());

        references.Keys.ShouldBe(["JasperFx/bobcat", "JasperFx/jasperfx"], ignoreOrder: true);
        references["JasperFx/bobcat"].ShouldBe([90]);
        references["JasperFx/jasperfx"].ShouldBe([486]);
    }

    [Fact]
    public async Task a_sweep_queries_each_repo_once_and_folds_the_cache()
    {
        var registry = registryWith(
            """
            schema: 1
            plan: sweep
            nodes:
              - id: a
                kind: issue
                repo: JasperFx/bobcat
                issue: 90
            """);

        var cache = new GitHubStatusCache();
        var client = new FakeClient
        {
            Respond = _ => """
                {
                  "data": { "repository": { "i90": {
                    "__typename": "Issue", "state": "CLOSED", "title": "done thing",
                    "assignees": { "nodes": [] }, "labels": { "nodes": [] },
                    "closedByPullRequestsReferences": { "nodes": [] }
                  } } }
                }
                """
        };
        var poller = new GitHubPoller(registry, cache, client, NullLogger<GitHubPoller>.Instance);

        (await poller.SweepAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        client.Queries.Count.ShouldBe(1);
        cache.Find("JasperFx/bobcat#90")!.State.ShouldBe("closed");

        // Second sweep: same answer, nothing changed.
        (await poller.SweepAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task a_failing_repo_keeps_its_last_observations()
    {
        var registry = registryWith(
            """
            schema: 1
            plan: flaky
            nodes:
              - id: a
                kind: issue
                repo: JasperFx/bobcat
                issue: 90
            """);

        var cache = new GitHubStatusCache();
        cache.Upsert(new GitHubObservation(
            "JasperFx/bobcat#90", "issue", "open", "still here", [], [], [], false, DateTimeOffset.UtcNow));

        var client = new FakeClient { Respond = _ => throw new HttpRequestException("rate limited") };
        var poller = new GitHubPoller(registry, cache, client, NullLogger<GitHubPoller>.Instance);

        (await poller.SweepAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        // A poll failure is absence of new evidence, not evidence of absence.
        cache.Find("JasperFx/bobcat#90")!.State.ShouldBe("open");
    }
}
