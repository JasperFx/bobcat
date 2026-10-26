using System.Text.Json;
using Bobcat.Monitoring;
using Bobcat.Ui.Builds;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// bobcat#651: the console keeps each watched project's latest build, so the board can say a
/// project no longer compiles. The receiver's record is a copy of the publisher's, so the round trip
/// is what pins them together.
/// </summary>
public class BuildStatusTests
{
    private static BuildStatusReported report(string state, int second, string suite = "Bank.Tests", string repository = "/code/bank")
        => new(suite, repository, "main", state, state == BuildStatusRegistry.BuildFailed ? "error CS1002" : null,
            DateTimeOffset.UnixEpoch.AddSeconds(second));

    [Fact]
    public void the_publishers_report_reads_as_the_receivers()
    {
        var sent = new BuildStatusReport("Bank.Tests", "/code/bank", "main", BuildStatusReport.BuildFailed, "error CS1002",
            DateTimeOffset.UnixEpoch);

        var read = JsonSerializer.Deserialize<BuildStatusReported>(
            JsonSerializer.Serialize(sent, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        read.ShouldBe(new BuildStatusReported("Bank.Tests", "/code/bank", "main", "build-failed", "error CS1002", DateTimeOffset.UnixEpoch));
        BuildStatusReport.Route.ShouldBe("/api/build-status");
        new[] { BuildStatusReport.Building, BuildStatusReport.BuildFailed, BuildStatusReport.Built }
            .ShouldBe([BuildStatusRegistry.Building, BuildStatusRegistry.BuildFailed, BuildStatusRegistry.Built]);
    }

    [Fact]
    public void each_project_keeps_its_latest_report_and_a_late_arrival_never_overwrites_it()
    {
        var builds = new BuildStatusRegistry();

        builds.Record(report(BuildStatusRegistry.Building, 1));
        builds.Record(report(BuildStatusRegistry.BuildFailed, 2));
        builds.Record(report(BuildStatusRegistry.Building, 1));
        builds.Record(report(BuildStatusRegistry.Built, 3, suite: "Other.Tests"));

        var all = builds.All();
        all.Select(x => (x.Suite, x.State)).ShouldBe([("Other.Tests", "built"), ("Bank.Tests", "build-failed")]);

        builds.Record(report(BuildStatusRegistry.Built, 4));
        builds.All().Single(x => x.Suite == "Bank.Tests").State.ShouldBe("built");
    }

    [Fact]
    public void a_report_without_a_suite_or_with_an_unknown_state_is_refused()
    {
        var builds = new BuildStatusRegistry();
        var accumulator = new SignalRBatchAccumulator(new ServiceCollection().BuildServiceProvider(), NullLogger<SignalRBatchAccumulator>.Instance);

        (BuildStatusEndpoints.Report(report("exploded", 1), builds, accumulator) as IStatusCodeHttpResult)!.StatusCode.ShouldBe(400);
        (BuildStatusEndpoints.Report(report("built", 1, suite: " "), builds, accumulator) as IStatusCodeHttpResult)!.StatusCode.ShouldBe(400);
        (BuildStatusEndpoints.Report(report("built", 1), builds, accumulator) as IStatusCodeHttpResult)!.StatusCode.ShouldBe(202);
        builds.All().ShouldHaveSingleItem();
    }
}
