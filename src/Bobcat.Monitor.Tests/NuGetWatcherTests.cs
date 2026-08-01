using System.Net;
using System.Text;
using Bobcat.Monitor.Coordination;
using Bobcat.Monitor.Coordination.GitHub;
using Bobcat.Monitor.Coordination.NuGet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Bobcat.Monitor.Tests;

public class PackageVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("1.9.0", "1.10.0")] // numeric, not lexical
    [InlineData("2.37.0", "2.37.0.1")] // four-part revision
    [InlineData("2.0.0-beta.2", "2.0.0")] // release outranks its prerelease
    [InlineData("2.0.0-alpha", "2.0.0-beta")]
    public void orders_versions(string smaller, string bigger)
        => PackageVersion.TryParse(bigger)!.CompareTo(PackageVersion.TryParse(smaller)!).ShouldBeGreaterThan(0);

    [Theory]
    [InlineData("garbage")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.-2")]
    [InlineData("")]
    public void rejects_garbage(string text) => PackageVersion.TryParse(text).ShouldBeNull();

    [Theory]
    [InlineData("2.37.0", "2.37.1", "fix", true)]
    [InlineData("2.37.0", "2.38.0", "fix", false)] // a bigger move than declared is NOT satisfaction
    [InlineData("2.37.0", "2.38.0", "minor", true)]
    [InlineData("2.37.0", "2.37.1", "minor", false)]
    [InlineData("2.37.0", "3.0.0", "minor", false)]
    [InlineData("2.37.0", "3.0.0", "major", true)]
    [InlineData("2.37.0", "2.38.0", "major", false)]
    public void bump_tiers_are_strict(string baseline, string candidate, string bump, bool satisfies)
    {
        PlanWire.TryBump(bump, out var kind).ShouldBeTrue();
        PackageVersion.TryParse(candidate)!
            .SatisfiesBumpFrom(PackageVersion.TryParse(baseline)!, kind)
            .ShouldBe(satisfies);
    }
}

public class FolderFeedTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "bobcat-folder-feed-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_path, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task reads_versions_from_nupkg_names_without_confusing_sibling_packages()
    {
        Directory.CreateDirectory(_path);
        foreach (var file in new[]
                 {
                     "JasperFx.Events.1.0.0.nupkg",
                     "JasperFx.Events.1.1.0-beta.nupkg",
                     "JasperFx.Events.1.1.0.symbols.nupkg", // never a version
                     "JasperFx.Events.Sqlite.9.9.9.nupkg", // longer id sharing the prefix
                     "notes.txt"
                 })
        {
            File.WriteAllText(Path.Combine(_path, file), "");
        }

        var versions = await new FolderFeed(_path)
            .GetVersionsAsync("JasperFx.Events", TestContext.Current.CancellationToken);

        versions.ShouldBe(["1.0.0", "1.1.0-beta"], ignoreOrder: true);
    }

    [Fact]
    public async Task a_missing_directory_is_an_empty_feed()
        => (await new FolderFeed(Path.Combine(_path, "nope"))
            .GetVersionsAsync("X", TestContext.Current.CancellationToken)).ShouldBeEmpty();
}

public class FlatContainerFeedTests
{
    private sealed class CannedHandler : HttpMessageHandler
    {
        public required Func<Uri, (HttpStatusCode Status, string Body)> Respond { get; init; }
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var (status, body) = Respond(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private const string ServiceIndex =
        """
        { "resources": [
            { "@id": "https://feed.example/other/", "@type": "SearchQueryService" },
            { "@id": "https://feed.example/flat", "@type": "PackageBaseAddress/3.0.0" }
        ] }
        """;

    [Fact]
    public async Task resolves_the_flat_container_from_the_service_index_once()
    {
        var handler = new CannedHandler
        {
            Respond = uri => uri.AbsoluteUri switch
            {
                "https://feed.example/index.json" => (HttpStatusCode.OK, ServiceIndex),
                "https://feed.example/flat/some.package/index.json" =>
                    (HttpStatusCode.OK, """{ "versions": ["1.0.0", "1.1.0"] }"""),
                _ => (HttpStatusCode.NotFound, "")
            }
        };

        var feed = new FlatContainerFeed(new HttpClient(handler), "https://feed.example/index.json", null, null);

        (await feed.GetVersionsAsync("Some.Package", TestContext.Current.CancellationToken))
            .ShouldBe(["1.0.0", "1.1.0"]);
        (await feed.GetVersionsAsync("Some.Package", TestContext.Current.CancellationToken)).Count.ShouldBe(2);

        // Three requests total: index once, package twice — the resolution is cached.
        handler.Requests.Count(x => x.AbsoluteUri.EndsWith("/index.json")
                                    && x.AbsoluteUri.Contains("feed.example/index")).ShouldBe(1);
    }

    [Fact]
    public async Task a_404_package_is_not_published_yet_not_an_error()
    {
        var handler = new CannedHandler
        {
            Respond = uri => uri.AbsoluteUri == "https://feed.example/index.json"
                ? (HttpStatusCode.OK, ServiceIndex)
                : (HttpStatusCode.NotFound, "")
        };

        var feed = new FlatContainerFeed(new HttpClient(handler), "https://feed.example/index.json", null, null);

        (await feed.GetVersionsAsync("Brand.New", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}

public class NuGetPollerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bobcat-nuget-poller-tests", Guid.NewGuid().ToString("N"));

    private string _plansPath => Path.Combine(_root, "plans");
    private string _feedPath => Path.Combine(_root, "feed");
    private string _dataPath => Path.Combine(_root, "data");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private (PlanRegistry Plans, NuGetPoller Poller, NuGetStatusCache Cache, NuGetBaselineStore Baselines) build(string planYaml)
    {
        Directory.CreateDirectory(_plansPath);
        Directory.CreateDirectory(_feedPath);
        File.WriteAllText(Path.Combine(_plansPath, "plan.yaml"), planYaml);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Monitor:Feeds:local:Path"] = _feedPath })
            .Build();

        var plans = new PlanRegistry(_plansPath);
        var cache = new NuGetStatusCache();
        var baselines = new NuGetBaselineStore(_dataPath);
        var feeds = new NuGetFeeds(configuration, new NoHttpFactory());
        var poller = new NuGetPoller(plans, feeds, cache, baselines, NullLogger<NuGetPoller>.Instance);

        return (plans, poller, cache, baselines);
    }

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no HTTP in this test");
    }

    private const string ShipPlan =
        """
        schema: 1
        plan: ship-it
        nodes:
          - id: ship
            kind: publish
            package: JasperFx.Events.Sqlite
            feed: local
            bump: minor
        """;

    private PlanStatusView status((PlanRegistry Plans, NuGetPoller Poller, NuGetStatusCache Cache, NuGetBaselineStore Baselines) world)
        => PlanStatus.For(world.Plans.Find("ship-it")!,
            new ObservationStores(new GitHubStatusCache(), new PackagePinCache(), world.Cache, world.Baselines));

    [Fact]
    public async Task the_full_arc_waiting_then_done_when_the_version_appears()
    {
        var world = build(ShipPlan);

        // Sweep 1: package absent — baseline is "did not exist", the first-publish case.
        await world.Poller.SweepAsync(TestContext.Current.CancellationToken);
        world.Baselines.TryGet("ship-it", "ship").ShouldBe("");
        var node = status(world).Nodes.Single();
        node.Status.ShouldBe("waiting");
        node.Detail.ShouldContain("first publish");
        node.Ready.ShouldBeTrue();

        // The publish happens: a .nupkg lands on the local feed.
        File.WriteAllText(Path.Combine(_feedPath, "JasperFx.Events.Sqlite.1.0.0.nupkg"), "");
        await world.Poller.SweepAsync(TestContext.Current.CancellationToken);

        node = status(world).Nodes.Single();
        node.Status.ShouldBe("done");
        node.Detail.ShouldBe("first publish observed: 1.0.0");
    }

    [Fact]
    public async Task an_existing_package_baselines_at_its_latest_and_demands_the_declared_bump()
    {
        Directory.CreateDirectory(_feedPath);
        File.WriteAllText(Path.Combine(_feedPath, "JasperFx.Events.Sqlite.2.37.0.nupkg"), "");
        var world = build(ShipPlan);

        await world.Poller.SweepAsync(TestContext.Current.CancellationToken);
        world.Baselines.TryGet("ship-it", "ship").ShouldBe("2.37.0");
        status(world).Nodes.Single().Status.ShouldBe("waiting");

        // A patch appears — the plan declared minor. Mismatch, reported, not reconciled.
        File.WriteAllText(Path.Combine(_feedPath, "JasperFx.Events.Sqlite.2.37.1.nupkg"), "");
        await world.Poller.SweepAsync(TestContext.Current.CancellationToken);
        var node = status(world).Nodes.Single();
        node.Status.ShouldBe("mismatch");
        node.Detail.ShouldContain("declared a minor bump from 2.37.0");

        // The declared bump arrives; the stray patch no longer matters.
        File.WriteAllText(Path.Combine(_feedPath, "JasperFx.Events.Sqlite.2.38.0.nupkg"), "");
        await world.Poller.SweepAsync(TestContext.Current.CancellationToken);
        node = status(world).Nodes.Single();
        node.Status.ShouldBe("done");
        node.Detail.ShouldBe("observed 2.38.0 (minor above 2.37.0)");
    }

    [Fact]
    public async Task baselines_survive_a_monitor_restart()
    {
        Directory.CreateDirectory(_feedPath);
        File.WriteAllText(Path.Combine(_feedPath, "JasperFx.Events.Sqlite.2.37.0.nupkg"), "");
        var before = build(ShipPlan);
        await before.Poller.SweepAsync(TestContext.Current.CancellationToken);

        // The publish happens while the monitor is down.
        File.WriteAllText(Path.Combine(_feedPath, "JasperFx.Events.Sqlite.2.38.0.nupkg"), "");

        // "Restart": everything rebuilt from disk. A re-baseline against 2.38.0 would wait
        // for 2.39.0 forever — the persisted baseline is what makes done detectable.
        var after = build(ShipPlan);
        await after.Poller.SweepAsync(TestContext.Current.CancellationToken);

        status(after).Nodes.Single().Status.ShouldBe("done");
    }

    [Fact]
    public async Task an_unconfigured_feed_is_a_wiring_fault_on_the_node()
    {
        var world = build(
            """
            schema: 1
            plan: ship-it
            nodes:
              - id: ship
                kind: publish
                package: X
                feed: nobody-configured-this
                bump: fix
            """);

        await world.Poller.SweepAsync(TestContext.Current.CancellationToken);

        var node = status(world).Nodes.Single();
        node.Status.ShouldBe("missing");
        node.Detail.ShouldContain("feed 'nobody-configured-this' is not configured");
        node.Ready.ShouldBeFalse();
    }

    [Fact]
    public async Task an_explicit_version_outranks_bump_derivation()
    {
        var world = build(
            """
            schema: 1
            plan: ship-it
            nodes:
              - id: ship
                kind: publish
                package: JasperFx.Events.Sqlite
                feed: local
                bump: minor
                version: 2.38.0
            """);

        Directory.CreateDirectory(_feedPath);
        File.WriteAllText(Path.Combine(_feedPath, "JasperFx.Events.Sqlite.2.39.0.nupkg"), "");
        await world.Poller.SweepAsync(TestContext.Current.CancellationToken);

        // 2.39.0 would satisfy the bump — but the author named 2.38.0, and that is the target.
        var node = status(world).Nodes.Single();
        node.Status.ShouldBe("waiting");
        node.Detail.ShouldBe("waiting for 2.38.0 — latest is 2.39.0");

        File.WriteAllText(Path.Combine(_feedPath, "JasperFx.Events.Sqlite.2.38.0.nupkg"), "");
        await world.Poller.SweepAsync(TestContext.Current.CancellationToken);
        status(world).Nodes.Single().Status.ShouldBe("done");
    }
}
