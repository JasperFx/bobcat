using System.Text.Json;
using Shouldly;
using Client = Bobcat.Monitoring;
using Wire = Bobcat.Console.Contracts;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issue #99's additions to the wire, pinned the same way <see cref="ContractRoundTripTests"/>
/// pins the rest: the client records in Bobcat.Monitoring must deserialize into the server
/// contracts with nothing lost, and the additions must be additive — an older publisher's JSON
/// with none of the new members still lands.
/// </summary>
public class ProgressContractRoundTripTests
{
    private static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
    private static readonly Guid runId = Guid.NewGuid();

    private static Wire.MonitorEvent roundTrip(Client.MonitorEvent @event)
    {
        var json = JsonSerializer.Serialize(@event, options);
        var wire = JsonSerializer.Deserialize<Wire.MonitorEvent>(json, options);
        wire.ShouldNotBeNull();
        return wire;
    }

    [Fact]
    public void scenario_started_carries_the_step_count()
    {
        var wire = roundTrip(new Client.ScenarioStarted(runId, "F/s", "F", "s", 1, DateTimeOffset.UtcNow, TotalSteps: 9))
            .ShouldBeOfType<Wire.ScenarioStarted>();

        wire.TotalSteps.ShouldBe(9);
    }

    [Fact]
    public void step_started_carries_position_count_and_elapsed()
    {
        var wire = roundTrip(new Client.StepStarted(runId, "F/s", "step-3", "When", "it ships", 3, 9, 1250))
            .ShouldBeOfType<Wire.StepStarted>();

        wire.StepNumber.ShouldBe(3);
        wire.TotalSteps.ShouldBe(9);
        wire.ScenarioElapsedMs.ShouldBe(1250);
    }

    [Fact]
    public void step_finished_carries_elapsed()
    {
        var wire = roundTrip(new Client.StepFinished(runId, "F/s", "step-3", "success", 40, null, 1290))
            .ShouldBeOfType<Wire.StepFinished>();

        wire.ScenarioElapsedMs.ShouldBe(1290);
    }

    [Fact]
    public void row_progress_round_trips()
    {
        var wire = roundTrip(new Client.StepProgress(runId, "F/s", "grammar", null, 140, 200, 3100))
            .ShouldBeOfType<Wire.StepProgress>();

        wire.Uid.ShouldBe("F/s");
        wire.StepId.ShouldBe("grammar");
        wire.Message.ShouldBeNull();
        wire.Row.ShouldBe(140);
        wire.TotalRows.ShouldBe(200);
        wire.ElapsedMs.ShouldBe(3100);
    }

    [Fact]
    public void wait_for_progress_round_trips()
    {
        var wire = roundTrip(new Client.StepProgress(runId, "F/s", "wait", "waiting… (attempt 4, 800ms); last value 2", null, null, 800))
            .ShouldBeOfType<Wire.StepProgress>();

        wire.Message.ShouldBe("waiting… (attempt 4, 800ms); last value 2");
        wire.Row.ShouldBeNull();
        wire.TotalRows.ShouldBeNull();
    }

    [Fact]
    public void an_older_publisher_without_the_new_members_still_lands()
    {
        // The JSON shape every publisher produced before #99: no totalSteps, stepNumber,
        // scenarioElapsedMs. Additive means additive.
        var scenario = JsonSerializer.Deserialize<Wire.MonitorEvent>(
            $$"""{"type":"scenario_started","runId":"{{runId}}","uid":"F/s","feature":"F","scenario":"s","attempt":1,"at":"2026-08-21T10:00:00Z"}""",
            options).ShouldBeOfType<Wire.ScenarioStarted>();
        scenario.TotalSteps.ShouldBeNull();

        var step = JsonSerializer.Deserialize<Wire.MonitorEvent>(
            $$"""{"type":"step_started","runId":"{{runId}}","uid":"F/s","stepId":"s1","kind":"Given","text":"a thing"}""",
            options).ShouldBeOfType<Wire.StepStarted>();
        step.StepNumber.ShouldBeNull();
        step.TotalSteps.ShouldBeNull();
        step.ScenarioElapsedMs.ShouldBeNull();

        var finished = JsonSerializer.Deserialize<Wire.MonitorEvent>(
            $$"""{"type":"step_finished","runId":"{{runId}}","uid":"F/s","stepId":"s1","status":"success","durationMs":5,"errorMessage":null}""",
            options).ShouldBeOfType<Wire.StepFinished>();
        finished.ScenarioElapsedMs.ShouldBeNull();
    }

    [Fact]
    public void step_progress_is_ingestible_in_a_batch()
    {
        var json = JsonSerializer.Serialize(new
        {
            events = new Client.MonitorEvent[]
            {
                new Client.StepStarted(runId, "F/s", "grammar", "Given", "rows", 1, 1, 0),
                new Client.StepProgress(runId, "F/s", "grammar", null, 1, 2, 3)
            }
        }, options);

        var batch = JsonSerializer.Deserialize<IngestBatch>(json, options).ShouldNotBeNull();
        batch.Events[1].ShouldBeOfType<Wire.StepProgress>().Row.ShouldBe(1);
    }
}
