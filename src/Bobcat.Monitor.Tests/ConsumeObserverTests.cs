using Bobcat.Monitor.Coordination;
using Bobcat.Monitor.Coordination.GitHub;
using Bobcat.Monitor.Coordination.NuGet;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Bobcat.Monitor.Tests;

public class PackagePinsTests
{
    private const string CentralProps =
        """
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
            <PackageVersion Include="JasperFx" Version="2.37.0" />
            <PackageVersion Include="WolverineFx.Http" Version="6.24.2" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void the_query_asks_for_both_conventional_locations()
    {
        var query = PackagePins.BuildQuery("JasperFx", "bobcat");

        query.ShouldContain("HEAD:Directory.Packages.props");
        query.ShouldContain("HEAD:src/Directory.Packages.props");
    }

    [Fact]
    public void parses_present_blobs_and_skips_absent_ones()
    {
        var files = PackagePins.ParseResponse(
            """
            { "data": { "repository": {
                "root": null,
                "src": { "text": "<Project />" }
            } } }
            """);

        files.ShouldBe([("src/Directory.Packages.props", "<Project />")]);
    }

    [Fact]
    public void finds_a_pin_case_insensitively()
    {
        var files = new[] { ("src/Directory.Packages.props", CentralProps) };

        PackagePins.FindPin(files, "jasperfx").ShouldBe(("2.37.0", "src/Directory.Packages.props"));
        PackagePins.FindPin(files, "Nope.Package").ShouldBe((null, null));
    }

    [Fact]
    public void the_first_file_in_precedence_order_wins()
    {
        var files = new[]
        {
            ("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="X" Version="1.0.0" /></ItemGroup></Project>"""),
            ("src/Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="X" Version="2.0.0" /></ItemGroup></Project>""")
        };

        PackagePins.FindPin(files, "X").Version.ShouldBe("1.0.0");
    }

    [Fact]
    public void a_file_that_will_not_parse_defines_nothing()
    {
        var files = new[]
        {
            ("Directory.Packages.props", "<Project><broken"),
            ("src/Directory.Packages.props", """<Project><ItemGroup><PackageReference Include="X" Version="3.0.0" /></ItemGroup></Project>""")
        };

        PackagePins.FindPin(files, "X").Version.ShouldBe("3.0.0");
    }
}

public class ConsumeStatusTests
{
    private static RegisteredPlan plan(string yaml)
    {
        var result = PlanParser.Parse(yaml);
        result.Errors.ShouldBeEmpty();
        return new RegisteredPlan("test", PlanSource.Pushed, result.Document, null, DateTimeOffset.UtcNow, []);
    }

    private const string Train =
        """
        schema: 1
        plan: train
        nodes:
          - id: ship
            kind: publish
            package: JasperFx.Events.Sqlite
            feed: local
            bump: minor
          - id: take
            kind: consume
            repo: JasperFx/bobcat
            package: JasperFx.Events.Sqlite
            depends_on: [ship]
        """;

    private static ObservationStores stores(
        string? pinnedVersion, string[]? feedVersions = null, string? baseline = "2.37.0", bool pinObserved = true)
    {
        var pins = new PackagePinCache();
        if (pinObserved)
        {
            pins.Upsert(new PackagePin(
                "JasperFx/bobcat", "JasperFx.Events.Sqlite", pinnedVersion,
                pinnedVersion is null ? null : "src/Directory.Packages.props", DateTimeOffset.UtcNow));
        }

        var nuGet = new NuGetStatusCache();
        if (feedVersions is not null)
        {
            nuGet.Upsert(new NuGetObservation("local", "JasperFx.Events.Sqlite", feedVersions, null, DateTimeOffset.UtcNow));
        }

        var baselines = new NuGetBaselineStore(
            Path.Combine(Path.GetTempPath(), "bobcat-consume-status-tests", Guid.NewGuid().ToString("N")));
        if (baseline is not null) baselines.Capture("test", "ship", baseline);

        return new ObservationStores(new GitHubStatusCache(), pins, nuGet, baselines,
            new Bobcat.Monitor.Runs.MonitorRunRegistry(
                Path.Combine(Path.GetTempPath(), "bobcat-consume-status-tests", Guid.NewGuid().ToString("N"), "runs")));
    }

    [Fact]
    public void done_means_the_pin_caught_up_to_what_was_actually_published()
    {
        // The feed shows the minor bump shipped; the repo pinned exactly it.
        var view = PlanStatus.For(plan(Train), stores("2.38.0", feedVersions: ["2.37.0", "2.38.0"]));

        var take = view.Nodes.Single(x => x.Id == "take");
        take.Status.ShouldBe("done");
        take.Detail.ShouldBe("pinned 2.38.0 (published 2.38.0)");
    }

    [Fact]
    public void a_stale_pin_waits_and_names_both_sides()
    {
        var view = PlanStatus.For(plan(Train), stores("2.37.0", feedVersions: ["2.37.0", "2.38.0"]));

        var take = view.Nodes.Single(x => x.Id == "take");
        take.Status.ShouldBe("waiting");
        take.Detail.ShouldBe("pinned 2.37.0, waiting for 2.38.0");
        take.Ready.ShouldBeTrue(); // the publish is done — updating the pin IS the ready work
    }

    [Fact]
    public void an_unpinned_package_is_the_upgrade_not_yet_made()
    {
        var view = PlanStatus.For(plan(Train), stores(null, feedVersions: ["2.37.0", "2.38.0"]));

        var take = view.Nodes.Single(x => x.Id == "take");
        take.Status.ShouldBe("waiting");
        take.Detail.ShouldBe("pinned nothing, waiting for 2.38.0");
    }

    [Fact]
    public void waits_without_a_target_while_the_upstream_publish_is_not_done()
    {
        var view = PlanStatus.For(plan(Train), stores("2.37.0", feedVersions: ["2.37.0"]));

        var take = view.Nodes.Single(x => x.Id == "take");
        take.Status.ShouldBe("waiting");
        take.Detail.ShouldContain("upstream publish not observed done yet");
        take.Ready.ShouldBeFalse(); // blocked behind the publish node
    }

    [Fact]
    public void no_pin_observation_yet_is_unknown()
    {
        var view = PlanStatus.For(plan(Train), stores(null, pinObserved: false));

        view.Nodes.Single(x => x.Id == "take").Status.ShouldBe("unknown");
    }

    [Fact]
    public void a_consume_without_any_publish_upstream_says_so()
    {
        var lonely = plan(
            """
            schema: 1
            plan: train
            nodes:
              - id: take
                kind: consume
                repo: JasperFx/bobcat
                package: JasperFx.Events.Sqlite
            """);

        var view = PlanStatus.For(lonely, stores("2.37.0"));

        var take = view.Nodes.Single();
        take.Status.ShouldBe("unknown");
        take.Detail.ShouldContain("no upstream publish node");
    }
}

public class PinSweepTests : IDisposable
{
    private readonly string _plansPath =
        Path.Combine(Path.GetTempPath(), "bobcat-pin-sweep-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_plansPath, recursive: true); }
        catch { }
    }

    private sealed class FakeClient : Bobcat.Monitor.Coordination.GitHub.IGitHubQueryClient
    {
        public List<string> Queries { get; } = [];

        public Task<string> PostQueryAsync(string query, CancellationToken ct)
        {
            Queries.Add(query);

            // The pin query asks for blobs; the issue query asks for issueOrPullRequest.
            return Task.FromResult(query.Contains("issueOrPullRequest")
                ? """{ "data": { "repository": {} } }"""
                : """
                  { "data": { "repository": {
                      "root": null,
                      "src": { "text": "<Project><ItemGroup><PackageVersion Include=\"JasperFx.Events.Sqlite\" Version=\"2.37.0\" /></ItemGroup></Project>" }
                  } } }
                  """);
        }
    }

    [Fact]
    public async Task the_sweep_observes_pins_for_consume_nodes()
    {
        Directory.CreateDirectory(_plansPath);
        File.WriteAllText(Path.Combine(_plansPath, "plan.yaml"),
            """
            schema: 1
            plan: pins
            nodes:
              - id: take
                kind: consume
                repo: JasperFx/bobcat
                package: JasperFx.Events.Sqlite
            """);

        var registry = new PlanRegistry(_plansPath);
        var pins = new PackagePinCache();
        var client = new FakeClient();
        var poller = new GitHubPoller(
            registry, new GitHubStatusCache(), pins, client, NullLogger<GitHubPoller>.Instance);

        (await poller.SweepAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        var pin = pins.Find("JasperFx/bobcat", "JasperFx.Events.Sqlite")!;
        pin.Version.ShouldBe("2.37.0");
        pin.Source.ShouldBe("src/Directory.Packages.props");

        // No bound issues in this plan — only the pin query went out.
        client.Queries.Single().ShouldContain("HEAD:Directory.Packages.props");

        // Same answer, no change.
        (await poller.SweepAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }
}
