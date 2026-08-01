using System.Text.Json;
using Bobcat.Monitor.Coordination;
using Bobcat.Monitor.Coordination.GitHub;
using Bobcat.Monitor.Coordination.NuGet;
using Bobcat.Monitor.Runs;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Bobcat.Monitor.Tests;

public class ClaimStoreTests
{
    [Fact]
    public void claims_conflict_by_holder_and_renew_for_the_same_agent()
    {
        var store = new ClaimStore();

        store.TryClaim("p", "n", "agent-a", TimeSpan.FromMinutes(5)).Succeeded.ShouldBeTrue();

        var conflict = store.TryClaim("p", "n", "agent-b", TimeSpan.FromMinutes(5));
        conflict.Succeeded.ShouldBeFalse();
        conflict.Conflict!.Agent.ShouldBe("agent-a");

        // Same agent renews rather than conflicts, and keeps the original ClaimedAt.
        var renewed = store.TryClaim("p", "n", "agent-a", TimeSpan.FromMinutes(5));
        renewed.Succeeded.ShouldBeTrue();
        renewed.Claim!.ClaimedAt.ShouldBe(conflict.Conflict.ClaimedAt);
    }

    [Fact]
    public void an_expired_lease_evaporates_which_is_the_whole_point()
    {
        var store = new ClaimStore();
        store.TryClaim("p", "n", "crashed-agent", TimeSpan.FromMilliseconds(30));

        Thread.Sleep(80);

        store.Find("p", "n").ShouldBeNull();
        store.TryClaim("p", "n", "agent-b", TimeSpan.FromMinutes(5)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void only_the_holder_reports_or_releases()
    {
        var store = new ClaimStore();
        store.TryClaim("p", "n", "agent-a", TimeSpan.FromMinutes(5));

        store.Report("p", "n", "agent-b", "sneaky", TimeSpan.FromMinutes(5)).ShouldBeNull();
        store.Release("p", "n", "agent-b").ShouldBeFalse();

        store.Report("p", "n", "agent-a", "fix is in the parser", TimeSpan.FromMinutes(5))!
            .Note.ShouldBe("fix is in the parser");
        store.Release("p", "n", "agent-a").ShouldBeTrue();
        store.Find("p", "n").ShouldBeNull();
    }
}

public class ClaimToolsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bobcat-claim-tools-tests", Guid.NewGuid().ToString("N"));

    private readonly PlanRegistry _registry;
    private readonly GitHubStatusCache _gitHub = new();
    private readonly NuGetStatusCache _nuGet = new();
    private readonly MonitorRunRegistry _runs;
    private readonly ObservationStores _stores;
    private readonly NuGetFeeds _feeds;

    private string _feedPath => Path.Combine(_root, "feed");

    public ClaimToolsTests()
    {
        var plansPath = Path.Combine(_root, "plans");
        Directory.CreateDirectory(plansPath);
        Directory.CreateDirectory(_feedPath);
        File.WriteAllText(Path.Combine(plansPath, "epic.yaml"),
            """
            schema: 1
            plan: epic
            repos:
              bobcat: JasperFx/bobcat
            nodes:
              - id: first
                kind: issue
                repo: bobcat
                issue: 1
              - id: second
                kind: issue
                repo: bobcat
                issue: 2
                depends_on: [first]
            """);

        _registry = new PlanRegistry(plansPath);
        _runs = new MonitorRunRegistry(Path.Combine(_root, "runs"));
        _stores = new ObservationStores(
            _gitHub, new PackagePinCache(), _nuGet,
            new NuGetBaselineStore(Path.Combine(_root, "data")), _runs, new ClaimStore());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Monitor:Feeds:local:Path"] = _feedPath })
            .Build();
        _feeds = new NuGetFeeds(configuration, new NoHttpFactory());
    }

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no HTTP in this test");
    }

    public void Dispose()
    {
        _runs.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private void observeIssue(int number, string state)
        => _gitHub.Upsert(new GitHubObservation(
            $"JasperFx/bobcat#{number}", "issue", state, "t", [], [], [], false, DateTimeOffset.UtcNow));

    private static JsonElement parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ready_work_carries_who_already_holds_it()
    {
        observeIssue(1, "open");

        _stores.Claims.TryClaim("epic", "first", "agent-a", TimeSpan.FromMinutes(5));
        var ready = parse(PlanTools.NextReadyNodes(_registry, _stores));

        ready.GetArrayLength().ShouldBe(1); // second is blocked
        ready[0].GetProperty("plan").GetString().ShouldBe("epic");
        ready[0].GetProperty("node").GetProperty("id").GetString().ShouldBe("first");
        ready[0].GetProperty("node").GetProperty("claimedBy").GetString().ShouldBe("agent-a");

        parse(PlanTools.NextReadyNodes(_registry, _stores, "nope"))
            .GetProperty("error").GetString().ShouldContain("no valid plan 'nope'");
    }

    [Fact]
    public void the_claim_lifecycle_updates_the_derived_status()
    {
        observeIssue(1, "open");

        var claimed = parse(PlanTools.ClaimNode(_registry, _stores, "epic", "first", "agent-a"));
        claimed.GetProperty("claimed").GetBoolean().ShouldBeTrue();
        claimed.GetProperty("readyWarning").ValueKind.ShouldBe(JsonValueKind.Null);

        // The dashboard's view now says claimed — asserted, but visibly leased.
        var status = PlanStatus.For(_registry.Find("epic")!, _stores);
        var first = status.Nodes.Single(x => x.Id == "first");
        first.Status.ShouldBe("claimed");
        first.ClaimedBy.ShouldBe("agent-a");

        parse(PlanTools.ClaimNode(_registry, _stores, "epic", "first", "agent-b"))
            .GetProperty("heldBy").GetString().ShouldBe("agent-a");

        parse(PlanTools.ReportNode(_registry, _stores, "epic", "first", "agent-a", "reproduced; fixing"))
            .GetProperty("reported").GetBoolean().ShouldBeTrue();
        PlanStatus.For(_registry.Find("epic")!, _stores)
            .Nodes.Single(x => x.Id == "first").Note.ShouldBe("reproduced; fixing");

        parse(PlanTools.ReleaseNode(_registry, _stores, "epic", "first", "agent-a"))
            .GetProperty("released").GetBoolean().ShouldBeTrue();
        PlanStatus.For(_registry.Find("epic")!, _stores)
            .Nodes.Single(x => x.Id == "first").Status.ShouldBe("open");
    }

    [Fact]
    public void done_nodes_refuse_claims_and_blocked_ones_warn()
    {
        observeIssue(1, "closed");
        observeIssue(2, "open");

        parse(PlanTools.ClaimNode(_registry, _stores, "epic", "first", "agent-a"))
            .GetProperty("error").GetString().ShouldContain("already done");

        // Claiming ahead of readiness is allowed — deliberately — but flagged.
        observeIssue(1, "open");
        parse(PlanTools.ClaimNode(_registry, _stores, "epic", "second", "agent-a"))
            .GetProperty("readyWarning").GetString().ShouldContain("not ready");
    }

    [Fact]
    public async Task await_dependencies_returns_when_the_upstream_closes()
    {
        observeIssue(1, "open");
        observeIssue(2, "open");

        var pending = PlanTools.AwaitDependencies(
            _registry, _stores, "epic", "second", timeoutSeconds: 30,
            cancellationToken: TestContext.Current.CancellationToken);

        // The dependency completes while the downstream agent is parked.
        await Task.Delay(400, TestContext.Current.CancellationToken);
        observeIssue(1, "closed");

        var result = parse(await pending);
        result.GetProperty("outcome").GetString().ShouldBe("ready");
        result.GetProperty("node").GetProperty("id").GetString().ShouldBe("second");
    }

    [Fact]
    public async Task await_dependencies_times_out_naming_the_blockers()
    {
        observeIssue(1, "open");
        observeIssue(2, "open");

        var result = parse(await PlanTools.AwaitDependencies(
            _registry, _stores, "epic", "second", timeoutSeconds: 1,
            cancellationToken: TestContext.Current.CancellationToken));

        result.GetProperty("outcome").GetString().ShouldBe("timeout");
        result.GetProperty("blockedOn")[0].GetProperty("id").GetString().ShouldBe("first");
    }

    [Fact]
    public async Task await_package_version_observes_an_already_published_version()
    {
        File.WriteAllText(Path.Combine(_feedPath, "JasperFx.Events.Sqlite.1.2.0.nupkg"), "");

        var result = parse(await PlanTools.AwaitPackageVersion(
            _feeds, _stores, "local", "JasperFx.Events.Sqlite", "1.0.0",
            cancellationToken: TestContext.Current.CancellationToken));

        result.GetProperty("outcome").GetString().ShouldBe("observed");
        result.GetProperty("version").GetString().ShouldBe("1.2.0");

        // The await doubled as an observation — the dashboard's cache learned too.
        _nuGet.Find("local", "JasperFx.Events.Sqlite")!.Versions.ShouldContain("1.2.0");
    }

    [Fact]
    public async Task await_package_version_times_out_with_the_latest_it_saw()
    {
        File.WriteAllText(Path.Combine(_feedPath, "JasperFx.Events.Sqlite.1.2.0.nupkg"), "");

        var result = parse(await PlanTools.AwaitPackageVersion(
            _feeds, _stores, "local", "JasperFx.Events.Sqlite", "2.0.0", timeoutSeconds: 1,
            cancellationToken: TestContext.Current.CancellationToken));

        result.GetProperty("outcome").GetString().ShouldBe("timeout");
        result.GetProperty("latest").GetString().ShouldBe("1.2.0");
    }

    [Fact]
    public async Task await_package_version_guards_its_inputs()
    {
        parse(await PlanTools.AwaitPackageVersion(
                _feeds, _stores, "local", "X", "garbage",
                cancellationToken: TestContext.Current.CancellationToken))
            .GetProperty("error").GetString().ShouldContain("not a parseable package version");

        parse(await PlanTools.AwaitPackageVersion(
                _feeds, _stores, "nobody", "X", "1.0.0",
                cancellationToken: TestContext.Current.CancellationToken))
            .GetProperty("error").GetString().ShouldContain("feed 'nobody' is not configured");
    }
}
