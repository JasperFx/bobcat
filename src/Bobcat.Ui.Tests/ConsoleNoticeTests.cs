using System.Text.Json;
using Bobcat.Ui.Builds;
using Bobcat.Ui.Contracts;
using Bobcat.Ui.Runners;
using Bobcat.Ui.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Util;

namespace Bobcat.Ui.Tests;

/// <summary>
/// bobcat#784: the console's own notices — the runner list, the build reports, an orphaned run —
/// ride the SignalR batch beside the events, so the page never polls for them.
/// </summary>
public class ConsoleNoticeTests : IDisposable
{
    private static readonly Guid runId = Guid.NewGuid();
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-notices-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private static SignalRBatchAccumulator newAccumulator()
        => new(new ServiceCollection().BuildServiceProvider(), NullLogger<SignalRBatchAccumulator>.Instance);

    private static RunnerRegistered registration(string id = "r-1")
        => new(id, "/code/bank", "main", "Bank.Tests", "projected", ["cold"], ["F/a", "F/b"]);

    [Fact]
    public void the_notices_are_named_the_way_relayToStore_switches_on_them()
    {
        typeof(RunOrphaned).ToMessageTypeName().ShouldBe("run_orphaned");
        typeof(RunnersChanged).ToMessageTypeName().ShouldBe("runners_changed");
        typeof(BuildStatusChanged).ToMessageTypeName().ShouldBe("build_status_changed");
    }

    [Fact]
    public void a_batch_ships_the_events_in_order_then_the_latest_notice_of_each_type()
    {
        var accumulator = newAccumulator();
        var started = new RunStarted(runId, "Demo", "/repo", "main", "in-process", DateTimeOffset.UtcNow, 2);

        accumulator.Notify(new BuildStatusChanged([]));
        accumulator.Enqueue([started]);
        accumulator.Notify(new RunnersChanged([]));
        var latest = new RunnersChanged([new RunnerView("r-1", "/code/bank", null, "main", "Bank.Tests", "projected", ["cold"], 2, true, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [])]);
        accumulator.Notify(latest);

        var batch = accumulator.DrainOnce().ShouldNotBeNull();
        batch.Items.Select(i => i.Type).ShouldBe(["run_started", "build_status_changed", "runners_changed"]);
        batch.Items[0].Data.ShouldBe(started);
        batch.Items[2].Data.ShouldBeSameAs(latest);

        accumulator.DrainOnce().ShouldBeNull();
    }

    [Fact]
    public void a_notice_serializes_in_the_envelope_shape_and_an_event_keeps_its_discriminator_beside_it()
    {
        var accumulator = newAccumulator();
        accumulator.Enqueue([new RunStarted(runId, "Demo", "/repo", "main", "in-process", DateTimeOffset.UtcNow, 2)]);
        accumulator.Notify(new RunOrphaned(runId, DateTimeOffset.UnixEpoch));

        var json = JsonSerializer.Serialize(accumulator.DrainOnce()!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.GetProperty("items");

        // The event's inner discriminator survives Data being declared as object (BatchedDataConverter).
        items[0].GetProperty("type").GetString().ShouldBe("run_started");
        items[0].GetProperty("data").GetProperty("type").GetString().ShouldBe("run_started");

        items[1].GetProperty("type").GetString().ShouldBe("run_orphaned");
        items[1].GetProperty("data").GetProperty("runId").GetGuid().ShouldBe(runId);
        items[1].GetProperty("data").GetProperty("lastEventAt").GetDateTimeOffset().ShouldBe(DateTimeOffset.UnixEpoch);
        items[1].GetProperty("data").TryGetProperty("type", out _).ShouldBeFalse();
    }

    [Fact]
    public void a_build_report_is_pushed_as_every_projects_latest()
    {
        var builds = new BuildStatusRegistry();
        var accumulator = newAccumulator();

        var result = BuildStatusEndpoints.Report(
            new BuildStatusReported("Bank.Tests", "/code/bank", "main", BuildStatusRegistry.BuildFailed, "error CS1002", DateTimeOffset.UnixEpoch),
            builds, accumulator);

        (result as IStatusCodeHttpResult)!.StatusCode.ShouldBe(202);
        var item = accumulator.DrainOnce().ShouldNotBeNull().Items.ShouldHaveSingleItem();
        item.Type.ShouldBe("build_status_changed");
        item.Data.ShouldBeOfType<BuildStatusChanged>().Builds.Single().State.ShouldBe("build-failed");
    }

    [Fact]
    public void a_refused_build_report_pushes_nothing()
    {
        var accumulator = newAccumulator();
        BuildStatusEndpoints.Report(new BuildStatusReported(" ", null, null, "built", null, DateTimeOffset.UnixEpoch), new BuildStatusRegistry(), accumulator);
        accumulator.DrainOnce().ShouldBeNull();
    }

    [Fact]
    public async Task the_runner_registry_says_when_anything_a_view_shows_has_changed()
    {
        var runners = new RunnerRegistry();
        var changes = 0;
        runners.Changed += () => changes++;

        runners.Register(registration(), "JasperFx/bank");
        changes.ShouldBe(1);

        runners.ReportStatus("r-1", RunnerWire.Rebuilding, null);
        changes.ShouldBe(2);
        runners.ReportStatus("nobody", RunnerWire.Rebuilding, null).ShouldBeFalse();
        changes.ShouldBe(2);

        var commandId = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");
        changes.ShouldBe(3);

        // A stream opening is the runner connecting; the command going out is its state moving;
        // the stream closing is the runner disconnecting.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in runners.Stream("r-1", null, TimeSpan.FromMinutes(1), cts.Token))
        {
            item.ShouldBeOfType<RunSpecsCommand>();
            break;
        }
        changes.ShouldBe(6);

        runners.Acknowledge("r-1", commandId, accepted: true, reason: null);
        changes.ShouldBe(7);
        runners.Acknowledge("r-1", Guid.NewGuid(), accepted: true, reason: null).ShouldBeFalse();
        changes.ShouldBe(7);
    }

    [Fact]
    public async Task runner_notices_push_the_list_the_endpoint_serves_on_every_change_and_on_a_commanded_runs_start()
    {
        using var runs = new MonitorRunRegistry(_dataPath, retention: TimeSpan.Zero, retainedRuns: 0);
        var runners = new RunnerRegistry();
        var accumulator = newAccumulator();
        var notices = new RunnerNotices(runners, runs, accumulator);
        await notices.StartAsync(CancellationToken.None);

        runners.Register(registration(), "JasperFx/bank");
        var commandId = runners.Enqueue("r-1", "run", ["F/a"], RunnerWire.Cold, "jeremy");

        var pushed = accumulator.DrainOnce().ShouldNotBeNull().Items.ShouldHaveSingleItem();
        pushed.Type.ShouldBe("runners_changed");
        var list = pushed.Data.ShouldBeOfType<RunnersChanged>().Runners;
        // The same shape the endpoint serves — compared member by member, since a view holds a list.
        var served = RunnerEndpoints.All(runners, runs).Single();
        var view = list.Single();
        (view.RunnerId, view.Suite, view.Connected, view.Specs).ShouldBe((served.RunnerId, served.Suite, served.Connected, served.Specs));
        view.Commands.Single().CommandId.ShouldBe(served.Commands.Single().CommandId);
        view.Commands.Single().RunId.ShouldBeNull();

        // The run the command caused names it on run_started — the one run event the list depends on.
        IngestionEndpoint.Ingest(
            new IngestBatch([new RunStarted(runId, "Bank.Tests", "/code/bank", "main", "in-process", DateTimeOffset.UtcNow, 1, Command: commandId.ToString())]),
            runs, accumulator, notices);

        var items = accumulator.DrainOnce().ShouldNotBeNull().Items;
        items.Select(i => i.Type).ShouldBe(["run_started", "runners_changed"]);
        items[1].Data.ShouldBeOfType<RunnersChanged>().Runners.Single().Commands.Single().RunId.ShouldBe(runId);

        // An uncommanded run's events are not the runner list's business.
        IngestionEndpoint.Ingest(new IngestBatch([new RunHeartbeat(runId, DateTimeOffset.UtcNow)]), runs, accumulator, notices);
        accumulator.DrainOnce()!.Items.Select(i => i.Type).ShouldBe(["run_heartbeat"]);

        await notices.StopAsync(CancellationToken.None);
        runners.ReportStatus("r-1", RunnerWire.Exited, null);
        accumulator.DrainOnce().ShouldBeNull();
    }
}
