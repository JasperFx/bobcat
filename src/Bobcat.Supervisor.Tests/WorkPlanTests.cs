using Shouldly;

namespace Bobcat.Supervisor.Tests;

public class WorkPlanTests
{
    private static WorkerTest Test(string displayName) => new(displayName, displayName);

    private static IReadOnlyList<WorkerTest> Class(string className, int count)
        => Enumerable.Range(1, count).Select(i => Test($"{className}.test_{i}")).ToList();

    // ── partition key ───────────────────────────────────────────────────────

    [Fact]
    public void a_dotted_name_partitions_on_the_declaring_class()
    {
        WorkPlan.ClassOf(Test("PersistenceTests.Postgresql.Transport.data_operations.move_to_incoming"))
            .ShouldBe("PersistenceTests.Postgresql.Transport.data_operations");
    }

    [Fact]
    public void a_bobcat_uid_partitions_on_the_feature()
    {
        // Bobcat's identity is "Feature/Scenario" everywhere — the retry budget, the MTP host,
        // and now the planner.
        WorkPlan.ClassOf(Test("Order Processing/places an order")).ShouldBe("Order Processing");
    }

    [Fact]
    public void theory_arguments_do_not_split_a_method_into_separate_partitions()
    {
        // A dot inside an argument would otherwise be read as the method separator, scattering
        // one theory's cases across lanes.
        WorkPlan.ClassOf(Test("Ns.SomeClass.a_theory(input: \"a.b.c\")")).ShouldBe("Ns.SomeClass");
    }

    [Fact]
    public void a_name_with_no_separator_is_its_own_partition()
    {
        WorkPlan.ClassOf(Test("standalone")).ShouldBe("standalone");
    }

    // ── the correctness rule ────────────────────────────────────────────────

    [Fact]
    public void a_class_is_never_split_across_lanes()
    {
        // The rule the whole design exists for. Measured against Wolverine: splitting per test
        // failed 1-4 of 78 non-deterministically (a class keyed its schema off a static counter),
        // while splitting per class passed 78/78.
        var tests = Class("A", 10).Concat(Class("B", 10)).Concat(Class("C", 10)).ToList();

        var lanes = WorkPlan.Build(tests, laneCount: 8);

        foreach (var lane in lanes)
        {
            lane.Uids.Select(WorkPlanTests.ClassNameOf).Distinct().Count()
                .ShouldBe(lane.Partitions.Count);
        }

        // and every class landed entirely in exactly one lane
        foreach (var className in new[] { "A", "B", "C" })
        {
            lanes.Count(lane => lane.Uids.Any(uid => uid.StartsWith(className + "."))).ShouldBe(1);
        }
    }

    [Fact]
    public void every_test_is_assigned_exactly_once()
    {
        var tests = Class("A", 7).Concat(Class("B", 3)).Concat(Class("C", 5)).ToList();

        var assigned = WorkPlan.Build(tests, laneCount: 3).SelectMany(l => l.Uids).ToList();

        assigned.Count.ShouldBe(15);
        assigned.Distinct().Count().ShouldBe(15);
        assigned.OrderBy(x => x).ShouldBe(tests.Select(t => t.Uid).OrderBy(x => x));
    }

    [Fact]
    public void there_are_never_more_lanes_than_partitions()
    {
        // Launching a process with nothing to do is pure cost.
        WorkPlan.Build(Class("A", 3).Concat(Class("B", 3)).ToList(), laneCount: 10).Count.ShouldBe(2);
    }

    // ── the single-lane path ────────────────────────────────────────────────

    [Fact]
    public void a_single_lane_preserves_discovery_order_exactly()
    {
        // MaxParallelWorkers defaults to 1, so this path must be byte-for-byte what the supervisor
        // did before parallelism existed — no reordering, no partitioning decision at all.
        var tests = new[] { Test("B.two"), Test("A.one"), Test("B.one"), Test("A.two") };

        var lanes = WorkPlan.Build(tests, laneCount: 1);

        lanes.ShouldHaveSingleItem().Uids.ShouldBe(["B.two", "A.one", "B.one", "A.two"]);
    }

    [Fact]
    public void no_tests_means_no_lanes()
    {
        WorkPlan.Build([], laneCount: 4).ShouldBeEmpty();
    }

    // ── balancing ───────────────────────────────────────────────────────────

    [Fact]
    public void without_durations_the_lanes_balance_on_test_count()
    {
        var tests = Class("Big", 10).Concat(Class("Small", 2)).Concat(Class("Medium", 6)).ToList();

        var lanes = WorkPlan.Build(tests, laneCount: 2);

        // Largest first: Big(10) to one lane, then Medium(6)+Small(2) to the other.
        lanes[0].Uids.Count.ShouldBe(10);
        lanes[1].Uids.Count.ShouldBe(8);
    }

    [Fact]
    public void known_durations_beat_test_count_for_balancing()
    {
        // The case count-balancing gets exactly wrong: one slow test outweighs many fast ones.
        // Measured on Wolverine, count-balanced lanes finished at 101.5s and 11.4s.
        var slow = Class("Slow", 1);
        var fast = Class("Fast", 20);

        var durations = new Dictionary<string, TimeSpan>
        {
            ["Slow.test_1"] = TimeSpan.FromSeconds(60)
        };
        foreach (var test in fast) durations[test.Uid] = TimeSpan.FromSeconds(1);

        var lanes = WorkPlan.Build([.. slow, .. fast], laneCount: 2, knownDurations: durations);

        // The 60s class is the heavier lane despite holding a twentieth of the tests.
        lanes[0].Uids.ShouldBe(["Slow.test_1"]);
        lanes[0].Estimate.ShouldBe(TimeSpan.FromSeconds(60));
        lanes[1].Uids.Count.ShouldBe(20);
    }

    [Fact]
    public void the_largest_partition_sets_the_floor_however_many_lanes_there_are()
    {
        // Wall clock is the slowest lane, so no fleet size beats the slowest single class. This
        // is why issue #56 (find the 61s test) and this feature are the same bottleneck.
        var durations = new Dictionary<string, TimeSpan> { ["Slow.test_1"] = TimeSpan.FromSeconds(60) };
        var tests = Class("Slow", 1).Concat(Class("Fast", 8)).ToList();
        foreach (var test in tests.Skip(1)) durations[test.Uid] = TimeSpan.FromSeconds(1);

        foreach (var laneCount in new[] { 2, 4, 8, 16 })
        {
            WorkPlan.Build(tests, laneCount, knownDurations: durations)
                .Max(l => l.Estimate).ShouldBe(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public void a_test_with_no_recorded_duration_is_charged_the_median_of_those_that_have()
    {
        // A newly added test must not be costed at a nominal second inside a suite of 30-second
        // integration tests — that would pile every new test into one lane.
        var tests = Class("Known", 3).Concat(Class("New", 1)).ToList();

        var durations = new Dictionary<string, TimeSpan>
        {
            ["Known.test_1"] = TimeSpan.FromSeconds(30),
            ["Known.test_2"] = TimeSpan.FromSeconds(30),
            ["Known.test_3"] = TimeSpan.FromSeconds(30)
        };

        var lanes = WorkPlan.Build(tests, laneCount: 2, knownDurations: durations);

        lanes.Single(l => l.Uids.Contains("New.test_1")).Estimate.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void the_plan_is_deterministic()
    {
        // Same inputs, same lanes — otherwise a rerun reshuffles which class shares a process
        // with which, and a contention bug becomes unreproducible.
        var tests = Class("A", 4).Concat(Class("B", 4)).Concat(Class("C", 4)).Concat(Class("D", 4)).ToList();

        var first = WorkPlan.Build(tests, laneCount: 3);
        var second = WorkPlan.Build(tests, laneCount: 3);

        first.Select(l => string.Join(",", l.Uids)).ShouldBe(second.Select(l => string.Join(",", l.Uids)));
    }

    [Fact]
    public void a_custom_partition_key_overrides_the_class_convention()
    {
        // The escape hatch for a suite whose real coupling is not the class — a shared database
        // named in a trait, say.
        var tests = new[] { Test("A.one"), Test("B.one"), Test("C.one") };

        var lanes = WorkPlan.Build(tests, laneCount: 3, partitionKey: _ => "everything together");

        lanes.ShouldHaveSingleItem().Uids.Count.ShouldBe(3);
    }

    private static string ClassNameOf(string uid) => WorkPlan.ClassOf(new WorkerTest(uid, uid));
}
