using System.Text.Json;
using Shouldly;
using Client = Bobcat.Monitoring;
using Wire = Bobcat.Console.Contracts;
using MonitorHost = Bobcat.Console;

namespace Bobcat.Console.Tests;

/// <summary>
/// The publisher records in Bobcat.Monitoring and the ingestion contracts in
/// Bobcat.Console.Contracts are deliberately NOT a shared assembly — the wire shape is the
/// contract. These round-trips are what keep the two sides honest: every client event must
/// deserialize into the matching server contract with nothing lost.
/// </summary>
public class ContractRoundTripTests
{
    private static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

    private static Wire.MonitorEvent roundTrip(Client.MonitorEvent @event)
    {
        var json = JsonSerializer.Serialize(@event, options);
        var wire = JsonSerializer.Deserialize<Wire.MonitorEvent>(json, options);
        wire.ShouldNotBeNull();
        return wire;
    }

    private static readonly Guid runId = Guid.NewGuid();

    [Fact]
    public void run_started_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.RunStarted(runId, "Suite", "/repo", "main", "in-process", at, 42, "epic/gate"))
            .ShouldBeOfType<Wire.RunStarted>();

        wire.RunId.ShouldBe(runId);
        wire.Suite.ShouldBe("Suite");
        wire.Repository.ShouldBe("/repo");
        wire.Branch.ShouldBe("main");
        wire.Mode.ShouldBe("in-process");
        wire.StartedAt.ShouldBe(at);
        wire.TotalScenarios.ShouldBe(42);
        wire.Tag.ShouldBe("epic/gate");
    }

    [Fact]
    public void run_started_without_a_tag_still_round_trips()
    {
        // An old publisher's JSON has no tag member at all — additive means additive.
        roundTrip(new Client.RunStarted(runId, "Suite", "/repo", null, "in-process", DateTimeOffset.UtcNow, null))
            .ShouldBeOfType<Wire.RunStarted>().Tag.ShouldBeNull();
    }

    [Fact]
    public void run_heartbeat_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.RunHeartbeat(runId, at)).ShouldBeOfType<Wire.RunHeartbeat>();
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void run_finished_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.RunFinished(runId, 1, 10, 2, 3, 1, at))
            .ShouldBeOfType<Wire.RunFinished>();

        wire.ExitCode.ShouldBe(1);
        wire.Passed.ShouldBe(10);
        wire.Failed.ShouldBe(2);
        wire.PassedOnRetry.ShouldBe(3);
        wire.Indeterminate.ShouldBe(1);
        wire.FinishedAt.ShouldBe(at);
    }

    [Fact]
    public void scenario_started_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.ScenarioStarted(runId, "F/s", "F", "s", 2, at))
            .ShouldBeOfType<Wire.ScenarioStarted>();

        wire.Uid.ShouldBe("F/s");
        wire.Feature.ShouldBe("F");
        wire.Scenario.ShouldBe("s");
        wire.Attempt.ShouldBe(2);
    }

    [Fact]
    public void scenario_finished_round_trips()
    {
        var wire = roundTrip(new Client.ScenarioFinished(runId, "F/s", "PassOnRetry", 2, 950, "boom"))
            .ShouldBeOfType<Wire.ScenarioFinished>();

        wire.Outcome.ShouldBe("PassOnRetry");
        wire.Attempts.ShouldBe(2);
        wire.DurationMs.ShouldBe(950);
        wire.ErrorMessage.ShouldBe("boom");
    }

    [Fact]
    public void retry_scheduled_round_trips()
    {
        var wire = roundTrip(new Client.RetryScheduled(runId, "F/s", 2, "RetryInProcess", "flaky broker"))
            .ShouldBeOfType<Wire.RetryScheduled>();

        wire.NextAttempt.ShouldBe(2);
        wire.Disposition.ShouldBe("RetryInProcess");
        wire.Reason.ShouldBe("flaky broker");
    }

    [Fact]
    public void step_started_round_trips()
    {
        var wire = roundTrip(new Client.StepStarted(runId, "F/s", "step-1", "Given", "a calculator"))
            .ShouldBeOfType<Wire.StepStarted>();

        wire.StepId.ShouldBe("step-1");
        wire.Kind.ShouldBe("Given");
        wire.Text.ShouldBe("a calculator");
    }

    [Fact]
    public void step_finished_round_trips()
    {
        var wire = roundTrip(new Client.StepFinished(runId, "F/s", "step-1", "failed", 12, "assertion"))
            .ShouldBeOfType<Wire.StepFinished>();

        wire.Status.ShouldBe("failed");
        wire.DurationMs.ShouldBe(12);
        wire.ErrorMessage.ShouldBe("assertion");
    }

    [Fact]
    public void lane_started_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.LaneStarted(runId, 2, ["F/a", "F/b"], at))
            .ShouldBeOfType<Wire.LaneStarted>();

        wire.Lane.ShouldBe(2);
        wire.Uids.ShouldBe(["F/a", "F/b"]);
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void lane_finished_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.LaneFinished(runId, 1, 7, true, at))
            .ShouldBeOfType<Wire.LaneFinished>();

        wire.Lane.ShouldBe(1);
        wire.Outcomes.ShouldBe(7);
        wire.Crashed.ShouldBeTrue();
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void resource_recycled_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.ResourceRecycled(runId, "rabbit", at))
            .ShouldBeOfType<Wire.ResourceRecycled>();

        wire.Resource.ShouldBe("rabbit");
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void worker_faulted_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.WorkerFaulted(runId, 3, "the worker exited with code 139", 139, "Segmentation fault", at))
            .ShouldBeOfType<Wire.WorkerFaulted>();

        wire.Lane.ShouldBe(3);
        wire.Fault.ShouldBe("the worker exited with code 139");
        wire.ExitCode.ShouldBe(139);
        wire.StandardError.ShouldBe("Segmentation fault");
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void a_worker_fault_outside_any_lane_round_trips_with_null_lane_and_no_exit_code()
    {
        // A one-test isolated process has no lane, and a worker that stopped responding without
        // dying has no exit code yet — both are facts, not gaps.
        var wire = roundTrip(new Client.WorkerFaulted(runId, null, "the worker stopped responding but is still running", null, null, DateTimeOffset.UtcNow))
            .ShouldBeOfType<Wire.WorkerFaulted>();

        wire.Lane.ShouldBeNull();
        wire.ExitCode.ShouldBeNull();
        wire.StandardError.ShouldBeNull();
    }

    [Fact]
    public void an_ingest_batch_of_mixed_events_deserializes_into_the_server_batch_shape()
    {
        var json = JsonSerializer.Serialize(new
        {
            events = new Client.MonitorEvent[]
            {
                new Client.RunStarted(runId, "Suite", "/repo", null, "mtp-host", DateTimeOffset.UtcNow, null),
                new Client.RunHeartbeat(runId, DateTimeOffset.UtcNow)
            }
        }, options);

        var batch = JsonSerializer.Deserialize<MonitorHost.IngestBatch>(json, options);

        batch.ShouldNotBeNull();
        batch.Events.Length.ShouldBe(2);
        batch.Events[0].ShouldBeOfType<Wire.RunStarted>();
        batch.Events[1].ShouldBeOfType<Wire.RunHeartbeat>();
    }
}
