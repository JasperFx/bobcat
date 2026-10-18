using System.Text.Json;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#705: Polecat could not tell whether ledger-balanced lanes were worse than count-balanced
/// ones or whether the runner was slower that day. A runner speed index against the known
/// durations, and the lane plan's prediction against what the lanes took, separate the two.
/// </summary>
public class RunnerSpeedTests
{
    private static TestReport test(string uid, double seconds, double? retrySeconds = null)
    {
        var attempts = new List<SupervisorAttempt>
        {
            new(1, new WorkerOutcome(uid, uid, retrySeconds is null ? WorkerTestState.Passed : WorkerTestState.Failed)
            {
                Duration = TimeSpan.FromSeconds(seconds)
            }, AttemptPlacement.Batched, retrySeconds is null ? Disposition.Pass : Disposition.RetryInProcess("flaky"))
        };

        if (retrySeconds is { } again)
        {
            attempts.Add(new SupervisorAttempt(2,
                new WorkerOutcome(uid, uid, WorkerTestState.Passed) { Duration = TimeSpan.FromSeconds(again) },
                AttemptPlacement.SameProcess, Disposition.Pass));
        }

        return new TestReport { Uid = uid, DisplayName = uid, Attempts = attempts };
    }

    private static Dictionary<string, TimeSpan> seconds(params (string Uid, double Seconds)[] known)
        => known.ToDictionary(k => k.Uid, k => TimeSpan.FromSeconds(k.Seconds));

    [Fact]
    public void the_speed_index_compares_first_attempts_with_the_known_durations_of_the_same_tests()
    {
        var results = new SupervisorResults
        {
            // A.one ran 30% slower than its history; B.one flaked, and its retry is not the
            // runner's fault; C.new has no history and stays out of both sides.
            Tests = [test("A.one", 13), test("B.one", 13, retrySeconds: 13), test("C.new", 50)],
            KnownTestDurations = seconds(("A.one", 10), ("B.one", 10), ("Gone.test", 99)),
            Duration = TimeSpan.FromSeconds(100)
        };

        var speed = RunTiming.For(results).RunnerSpeed.ShouldNotBeNull();
        speed.Matched.ShouldBe(2);
        speed.ThisRun.ShouldBe(TimeSpan.FromSeconds(26));
        speed.Known.ShouldBe(TimeSpan.FromSeconds(20));
        speed.Index.ShouldBe(1.3, 0.0001);

        RunReport.ToText(results).ShouldContain("runner speed 1.30x the known durations of the same 2 test(s)");

        var json = JsonDocument.Parse(RunReport.ToJson(results)).RootElement.GetProperty("timing");
        json.GetProperty("runnerSpeed").GetProperty("index").GetDouble().ShouldBe(1.3, 0.0001);
        json.GetProperty("runnerSpeed").GetProperty("matched").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void no_known_durations_or_none_matching_means_no_speed_index()
    {
        var none = new SupervisorResults { Tests = [test("A.one", 3)], Duration = TimeSpan.FromSeconds(5) };
        RunTiming.For(none).RunnerSpeed.ShouldBeNull();
        JsonDocument.Parse(RunReport.ToJson(none)).RootElement.GetProperty("timing")
            .GetProperty("runnerSpeed").ValueKind.ShouldBe(JsonValueKind.Null);

        var foreign = new SupervisorResults
        {
            Tests = [test("A.one", 3)],
            KnownTestDurations = seconds(("another-machine", 3)),
            Duration = TimeSpan.FromSeconds(5)
        };
        RunTiming.For(foreign).RunnerSpeed.ShouldBeNull();
        RunReport.ToText(foreign).ShouldNotContain("runner speed");
    }

    [Fact]
    public void a_duration_balanced_plan_reports_predicted_against_realised_makespan()
    {
        var results = new SupervisorResults
        {
            Tests = [test("A.one", 24), test("A.two", 24), test("B.one", 10), test("B.two", 10)],
            KnownTestDurations = seconds(("A.one", 20), ("A.two", 20), ("B.one", 20), ("B.two", 20)),
            DurationCoverage = new DurationCoverage(4, 4),
            LanePlan =
            [
                new WorkLane(0, ["A.one", "A.two"], TimeSpan.FromSeconds(40)),
                new WorkLane(1, ["B.one", "B.two"], TimeSpan.FromSeconds(40))
            ],
            LaneElapsed = new Dictionary<int, TimeSpan>
            {
                [0] = TimeSpan.FromSeconds(50),
                [1] = TimeSpan.FromSeconds(25)
            },
            Duration = TimeSpan.FromSeconds(55),
            Lanes = 2
        };

        var timing = RunTiming.For(results);
        timing.BalancedByDuration.ShouldBeTrue();
        timing.LanePlan.Count.ShouldBe(2);
        timing.LanePlan[0].Predicted.ShouldBe(TimeSpan.FromSeconds(40));
        timing.LanePlan[0].TestTime.ShouldBe(TimeSpan.FromSeconds(48));
        timing.LanePlan[1].Elapsed.ShouldBe(TimeSpan.FromSeconds(25));

        timing.PredictedMakespan.ShouldBe(TimeSpan.FromSeconds(40));
        timing.RunnerSpeed!.Index.ShouldBe(68.0 / 80, 0.0001);
        timing.SpeedAdjustedMakespan!.Value.TotalSeconds.ShouldBe(34, 0.001);
        timing.RealisedMakespan.ShouldBe(TimeSpan.FromSeconds(48));
        timing.RealisedLaneWallClock.ShouldBe(TimeSpan.FromSeconds(50));

        // The planner expected an even split; the lanes came in at 50s and 25s.
        timing.PredictedImbalance!.Value.ShouldBe(1.0, 0.0001);
        timing.RealisedImbalance!.Value.ShouldBe(50.0 / 37.5, 0.0001);

        var text = RunReport.ToText(results);
        text.ShouldContain("lane plan (balanced by duration): predicted makespan");
        text.ShouldContain("slowest lane over the mean 1.00x planned, 1.33x realised");
        text.ShouldContain("lane 0: 2 test(s), predicted 40.0s, test time 48.0s, wall clock 50.0s");

        var plan = JsonDocument.Parse(RunReport.ToJson(results)).RootElement
            .GetProperty("timing").GetProperty("lanePlan");
        plan.GetProperty("balancedBy").GetString().ShouldBe("duration");
        plan.GetProperty("predictedMakespanMs").GetDouble().ShouldBe(40_000);
        plan.GetProperty("realisedLaneWallClockMs").GetDouble().ShouldBe(50_000);
        plan.GetProperty("lanes").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void a_count_balanced_plan_predicts_no_time_but_still_compares_the_balance()
    {
        var results = new SupervisorResults
        {
            Tests = [test("A.one", 30), test("B.one", 10)],
            DurationCoverage = new DurationCoverage(0, 2),
            LanePlan =
            [
                new WorkLane(0, ["A.one"], TimeSpan.FromSeconds(1)),
                new WorkLane(1, ["B.one"], TimeSpan.FromSeconds(1))
            ],
            Duration = TimeSpan.FromSeconds(31),
            Lanes = 2
        };

        var timing = RunTiming.For(results);
        timing.BalancedByDuration.ShouldBeFalse();
        timing.PredictedMakespan.ShouldBeNull();
        timing.SpeedAdjustedMakespan.ShouldBeNull();
        timing.LanePlan[0].Predicted.ShouldBeNull();

        // No lane was timed, so the realised balance falls back to each lane's test time.
        timing.RealisedLaneWallClock.ShouldBeNull();
        timing.PredictedImbalance!.Value.ShouldBe(1.0, 0.0001);
        timing.RealisedImbalance!.Value.ShouldBe(1.5, 0.0001);

        RunReport.ToText(results).ShouldContain("lane plan (balanced by test count, so no predicted time)");
        var plan = JsonDocument.Parse(RunReport.ToJson(results)).RootElement
            .GetProperty("timing").GetProperty("lanePlan");
        plan.GetProperty("balancedBy").GetString().ShouldBe("count");
        plan.GetProperty("predictedMakespanMs").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void one_lane_has_no_plan_to_judge()
    {
        var results = new SupervisorResults
        {
            Tests = [test("A.one", 3)],
            LanePlan = [new WorkLane(0, ["A.one"], TimeSpan.FromSeconds(1))],
            Duration = TimeSpan.FromSeconds(5),
            Lanes = 1
        };

        RunTiming.For(results).LanePlan.ShouldBeEmpty();
        RunReport.ToText(results).ShouldNotContain("lane plan");
        JsonDocument.Parse(RunReport.ToJson(results)).RootElement.GetProperty("timing")
            .GetProperty("lanePlan").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task a_supervised_run_keeps_its_lane_plan_lane_times_and_known_durations()
    {
        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.InClass("A", "one"), FakeWorkerFactory.InClass("A", "two"),
                FakeWorkerFactory.InClass("B", "one")
            ],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            Duration = (_, _) => TimeSpan.FromSeconds(2)
        };

        var known = seconds(("A.one", 1), ("A.two", 1), ("B.one", 1));
        var run = await new Supervisor(factory) { MaxParallelWorkers = 2, KnownTestDurations = known }.Run();

        run.KnownTestDurations.ShouldBeSameAs(known);
        run.LanePlan.Count.ShouldBe(2);
        run.LaneElapsed.Keys.Order().ShouldBe([0, 1]);

        var timing = RunTiming.For(run);
        timing.RunnerSpeed!.Index.ShouldBe(2.0, 0.0001);
        timing.BalancedByDuration.ShouldBeTrue();
        timing.PredictedMakespan.ShouldBe(TimeSpan.FromSeconds(2));
        timing.RealisedMakespan.ShouldBe(TimeSpan.FromSeconds(4));
    }
}
