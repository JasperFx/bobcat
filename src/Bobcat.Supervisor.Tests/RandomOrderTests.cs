using System.Text.Json;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#578: <c>Supervisor.Order = Random</c> runs each lane's class partitions one request at a
/// time in the seed's order, says its seed, and leaves a declared run exactly as it was.
/// </summary>
public class RandomOrderTests
{
    private static IReadOnlyList<WorkerTest> classes(params (string Name, int Count)[] classes)
        => classes
            .SelectMany(c => Enumerable.Range(1, c.Count).Select(i => FakeWorkerFactory.InClass(c.Name, $"test_{i}")))
            .ToList();

    private static FakeWorkerFactory passing(params (string Name, int Count)[] shape)
        => new() { Tests = classes(shape), Outcome = (_, _, _) => WorkerTestState.Passed };

    [Fact]
    public async Task a_random_order_runs_one_request_per_class_in_the_seeds_order()
    {
        var factory = passing(("A", 2), ("B", 2), ("C", 2), ("D", 2), ("E", 2));

        var results = await new Supervisor(factory) { Order = RunOrder.Random, Seed = 42 }.Run();

        results.CleanPasses.Count.ShouldBe(10);
        results.Order.ShouldBe(RunOrder.Random);
        results.Seed.ShouldBe(42);

        // One worker, five requests, one class each, in Shuffle(42)'s order: E, A, B, C, D.
        var worker = factory.RunningWorkers.ShouldHaveSingleItem();
        worker.Runs.Count.ShouldBe(5);
        worker.Runs.Select(r => WorkPlan.ClassOf(new WorkerTest(r![0], r[0]))).ShouldBe(["E", "A", "B", "C", "D"]);
        worker.Runs.ShouldAllBe(r => r!.Count == 2);

        // And the order inside a class is the declared one.
        worker.Runs[0].ShouldBe(["E.test_1", "E.test_2"]);
    }

    [Fact]
    public async Task the_lanes_take_the_shuffled_classes_round_robin()
    {
        var factory = passing(("A", 2), ("B", 2), ("C", 2), ("D", 2), ("E", 2));

        var results = await new Supervisor(factory) { Order = RunOrder.Random, Seed = 42, MaxParallelWorkers = 2 }.Run();

        results.CleanPasses.Count.ShouldBe(10);
        var lanes = factory.RunningWorkers.OrderBy(w => w.Launch.Lane).ToList();
        lanes.Count.ShouldBe(2);
        lanes[0].Runs.Select(r => r![0]).ShouldBe(["E.test_1", "B.test_1", "D.test_1"]);
        lanes[1].Runs.Select(r => r![0]).ShouldBe(["A.test_1", "C.test_1"]);

        results.LanePlan.Count.ShouldBe(2);
        results.LanePlan[0].Partitions.ShouldBe(["E", "B", "D"]);
    }

    [Fact]
    public async Task a_declared_order_is_still_one_request_per_lane()
    {
        var factory = passing(("A", 2), ("B", 2), ("C", 2));

        var results = await new Supervisor(factory).Run();

        results.Order.ShouldBe(RunOrder.Declared);
        results.Seed.ShouldBeNull();
        factory.RunningWorkers.ShouldHaveSingleItem().Runs.ShouldHaveSingleItem()!.Count.ShouldBe(6);
    }

    [Fact]
    public async Task no_seed_mints_one_and_reports_it()
    {
        var factory = passing(("A", 1), ("B", 1));
        var log = new List<string>();

        var results = await new Supervisor(factory) { Order = RunOrder.Random, Log = log.Add }.Run();

        var seed = results.Seed.ShouldNotBeNull();
        log.ShouldContain($"Order: random, seed {seed}");

        var text = RunReport.ToText(results);
        text.ShouldContain($"Order: random, seed {seed}");
        text.ShouldContain($"--order random --seed {seed}");

        var json = JsonDocument.Parse(RunReport.ToJson(results)).RootElement;
        json.GetProperty("order").GetString().ShouldBe("random");
        json.GetProperty("seed").GetInt32().ShouldBe(seed);
    }

    [Fact]
    public void a_declared_report_says_so_and_has_no_seed()
    {
        var results = new SupervisorResults { Tests = [] };

        RunReport.ToText(results).ShouldNotContain("Order:");

        var json = JsonDocument.Parse(RunReport.ToJson(results)).RootElement;
        json.GetProperty("order").GetString().ShouldBe("declared");
        json.GetProperty("seed").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task a_worker_that_dies_mid_lane_leaves_the_classes_it_never_reached_indeterminate_with_the_fault()
    {
        // Every request to the lane's worker faults, so the first class's tests are completed by the
        // client (as for any crash) and the classes after it by the lane — all of them explained.
        var factory = new FakeWorkerFactory
        {
            Tests = classes(("A", 1), ("B", 1), ("C", 1)),
            Outcome = (_, _, _) => null,
            Fault = worker => worker.Launch.Purpose == WorkerPurpose.Lane ? "exit code 134" : null
        };

        var results = await new Supervisor(factory) { Order = RunOrder.Random, Seed = 1 }.Run();

        results.Indeterminate.Count.ShouldBe(3);
        results.Tests.ShouldAllBe(t => t.Final.Outcome.ErrorMessage != null && t.Final.Outcome.ErrorMessage.Contains("exit code 134"));
        results.WorkerFaults.Count.ShouldBe(1);
        factory.RunningWorkers.ShouldHaveSingleItem().Runs.Count.ShouldBe(1);
    }
}
