using System.Text.Json;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#664: parallel efficiency read 1.56x on Marten's single sequential worker. The overlap
/// was xUnit's own parallelism inside the worker, and the figure implied lanes that overlapped.
/// </summary>
public class LaneConcurrencyTests
{
    private static SupervisorResults results(int lanes, TimeSpan wallClock, TimeSpan busy, params double[] testSeconds)
        => new()
        {
            Tests = testSeconds.Select((seconds, i) => new TestReport
            {
                Uid = $"t{i}",
                DisplayName = $"Suite.t{i}",
                Attempts =
                [
                    new SupervisorAttempt(1,
                        new WorkerOutcome($"t{i}", $"Suite.t{i}", WorkerTestState.Passed)
                        {
                            Duration = TimeSpan.FromSeconds(seconds)
                        },
                        AttemptPlacement.Batched, Disposition.Pass)
                ]
            }).ToList(),
            Duration = wallClock,
            WorkerBusyTime = busy,
            Lanes = lanes
        };

    [Fact]
    public void one_lane_attributes_every_overlap_to_the_test_framework()
    {
        // Marten's shape: one worker busy for the whole run, 1.56x the run's worth of test time.
        var run = results(1, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100), 78, 78);
        var timing = RunTiming.For(run);

        timing.ParallelEfficiency!.Value.ShouldBe(1.56, 0.001);
        timing.LaneConcurrency!.Value.ShouldBe(1.0, 0.001);
        timing.InWorkerConcurrency!.Value.ShouldBe(1.56, 0.001);

        var text = RunReport.ToText(run);
        text.ShouldContain("1.56x parallel efficiency, all of it inside one worker");
        text.ShouldContain("one lane, so none of it is the supervisor's");
    }

    [Fact]
    public void several_lanes_split_the_overlap_between_lanes_and_the_framework()
    {
        // Two lanes busy side by side for the whole run, each running its tests one at a time.
        var run = results(2, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(100), 25, 25, 25, 25);
        var timing = RunTiming.For(run);

        timing.ParallelEfficiency!.Value.ShouldBe(2.0, 0.001);
        timing.LaneConcurrency!.Value.ShouldBe(2.0, 0.001);
        timing.InWorkerConcurrency!.Value.ShouldBe(1.0, 0.001);

        RunReport.ToText(run).ShouldContain(
            "2.00x parallel efficiency: 2.00x from 2 lanes running side by side, 1.00x in-process test parallelism");

        var json = JsonDocument.Parse(RunReport.ToJson(run)).RootElement.GetProperty("timing");
        json.GetProperty("lanes").GetInt32().ShouldBe(2);
        json.GetProperty("laneConcurrency").GetDouble().ShouldBe(2.0, 0.001);
        json.GetProperty("inWorkerConcurrency").GetDouble().ShouldBe(1.0, 0.001);
        json.GetProperty("workerBusyMs").GetDouble().ShouldBe(100_000);
    }

    [Fact]
    public void unmeasured_worker_time_keeps_the_bare_figure_and_says_nothing_it_cannot_know()
    {
        var run = results(0, TimeSpan.FromSeconds(10), TimeSpan.Zero, 5);
        var timing = RunTiming.For(run);

        timing.LaneConcurrency.ShouldBeNull();
        timing.InWorkerConcurrency.ShouldBeNull();
        RunReport.ToText(run).ShouldContain("0.50x parallel efficiency");

        var json = JsonDocument.Parse(RunReport.ToJson(run)).RootElement.GetProperty("timing");
        json.GetProperty("workerBusyMs").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("laneConcurrency").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task a_supervised_run_measures_its_lanes_and_their_busy_time()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.InClass("A", "one"), FakeWorkerFactory.InClass("B", "one")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (_, _) => Task.Delay(20)
        };

        var run = await new Supervisor(factory) { MaxParallelWorkers = 2 }.Run();

        run.Lanes.ShouldBe(2);
        run.WorkerBusyTime.ShouldBeGreaterThan(TimeSpan.Zero);
    }
}
