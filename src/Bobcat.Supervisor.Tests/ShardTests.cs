using System.Text.Json.Nodes;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#576: <c>bobcat run --shard i/n</c> splits the class partitions over legs that run on
/// different machines, each computing only its own share — so the split has to be a pure function of
/// the test list and the known durations, the legs disjoint, and their union the whole run.
/// </summary>
public class ShardTests
{
    private static WorkerTest test(string displayName) => new(displayName, displayName);

    private static IReadOnlyList<WorkerTest> testsInClass(string className, int count)
        => Enumerable.Range(1, count).Select(i => test($"{className}.test_{i}")).ToList();

    /// <summary>A suite of random shape: 1–30 classes of 1–12 tests, interleaved as discovery might.</summary>
    private static IReadOnlyList<WorkerTest> randomSuite(int seed)
    {
        var random = new Random(seed);
        var tests = Enumerable.Range(0, random.Next(1, 31))
            .SelectMany(c => testsInClass($"Ns.Class{c:D2}", random.Next(1, 13)))
            .ToList();
        return tests.OrderBy(_ => random.Next()).ToList();
    }

    [Fact]
    public void legs_are_disjoint_and_cover_every_test_for_any_suite_and_leg_count()
    {
        for (var suite = 0; suite < 40; suite++)
        {
            var tests = randomSuite(suite);
            var random = new Random(suite);
            var durations = suite % 2 == 0
                ? null
                : tests.Where(_ => random.Next(3) > 0)
                    .ToDictionary(t => t.DisplayName, _ => TimeSpan.FromMilliseconds(random.Next(10, 5000)));

            for (var count = 1; count <= 8; count++)
            {
                var legs = WorkPlan.Legs(tests, count, knownDurations: durations);

                legs.Count.ShouldBe(count);
                var uids = legs.SelectMany(l => l.Tests.Select(t => t.Uid)).ToList();
                uids.Count.ShouldBe(tests.Count, $"suite {suite}, {count} legs: a test was run twice or dropped");
                uids.ShouldBe(tests.Select(t => t.Uid), ignoreOrder: true);

                // A class is never split between legs.
                legs.SelectMany(l => l.Partitions).Count().ShouldBe(tests.Select(WorkPlan.ClassOf).Distinct().Count());
                foreach (var leg in legs)
                {
                    leg.Tests.Select(WorkPlan.ClassOf).Distinct().Order(StringComparer.Ordinal).ShouldBe(leg.Partitions);
                }
            }
        }
    }

    [Fact]
    public void a_leg_is_a_pure_function_of_the_tests_and_durations_not_of_discovery_order()
    {
        for (var suite = 0; suite < 20; suite++)
        {
            var tests = randomSuite(suite);
            var reordered = tests.Reverse().ToList();

            for (var count = 1; count <= 8; count++)
            {
                for (var index = 1; index <= count; index++)
                {
                    var shard = new RunShard(index, count);
                    var a = WorkPlan.Shard(tests, shard).Tests.Select(t => t.Uid).Order(StringComparer.Ordinal).ToList();
                    var b = WorkPlan.Shard(reordered, shard).Tests.Select(t => t.Uid).Order(StringComparer.Ordinal).ToList();
                    b.ShouldBe(a, $"suite {suite}, shard {shard}: another machine's discovery order changed the leg");
                }
            }
        }
    }

    [Fact]
    public void a_legs_tests_keep_their_discovery_order()
    {
        WorkerTest[] tests = [test("B.one"), test("A.one"), test("B.two"), test("A.two")];

        WorkPlan.Shard(tests, new RunShard(1, 1)).Tests.ShouldBe(tests);
    }

    [Fact]
    public void without_durations_legs_are_balanced_by_test_count()
    {
        var tests = testsInClass("Big", 6).Concat(testsInClass("Mid", 4)).Concat(testsInClass("Small", 2)).ToList();

        var legs = WorkPlan.Legs(tests, 2);

        legs[0].Partitions.ShouldBe(["Big"]);
        legs[1].Partitions.ShouldBe(["Mid", "Small"]);
        legs[0].BalancedByDuration.ShouldBeFalse();
        legs[0].ToString().ShouldBe(
            "Shard 1/2: 1 of 3 class(es) (6 of 12 test(s)), legs balanced by test count (no known durations)");
    }

    [Fact]
    public void known_durations_outweigh_test_counts()
    {
        // Few but slow beats many but quick, exactly as lanes are balanced.
        var tests = testsInClass("Many", 6).Concat(testsInClass("Slow", 2)).Concat(testsInClass("Other", 1)).ToList();
        var durations = tests.ToDictionary(t => t.DisplayName,
            t => t.DisplayName.StartsWith("Slow", StringComparison.Ordinal) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(1));

        var legs = WorkPlan.Legs(tests, 2, knownDurations: durations);

        legs[0].Partitions.ShouldBe(["Slow"]);
        legs[1].Partitions.ShouldBe(["Many", "Other"]);
        legs[0].BalancedByDuration.ShouldBeTrue();
        legs[0].ToString().ShouldBe(
            "Shard 1/2: 1 of 3 class(es) (2 of 9 test(s)), estimated 2m00s of 2m07s (Known durations matched 9/9 test(s))");
    }

    [Fact]
    public void a_ledger_that_matched_nothing_says_so()
    {
        var legs = WorkPlan.Legs(testsInClass("A", 2), 2,
            knownDurations: new Dictionary<string, TimeSpan> { ["somewhere else"] = TimeSpan.FromSeconds(1) });

        legs[0].ToString().ShouldEndWith("legs balanced by test count (known durations matched 0/2 test(s))");
    }

    [Fact]
    public void ties_are_broken_by_class_name_so_every_machine_agrees()
    {
        var tests = testsInClass("Zeta", 2).Concat(testsInClass("Alpha", 2)).ToList();

        var legs = WorkPlan.Legs(tests, 2);

        legs[0].Partitions.ShouldBe(["Alpha"]);
        legs[1].Partitions.ShouldBe(["Zeta"]);
    }

    [Fact]
    public void legs_beyond_the_number_of_classes_are_empty()
    {
        var legs = WorkPlan.Legs(testsInClass("Only", 3), 3);

        legs[0].Tests.Count.ShouldBe(3);
        legs[1].Tests.ShouldBeEmpty();
        legs[2].Tests.ShouldBeEmpty();
        legs[2].ToString().ShouldStartWith("Shard 3/3: 0 of 1 class(es) (0 of 3 test(s))");
    }

    [Theory]
    [InlineData("2/4", 2, 4)]
    [InlineData(" 1 / 1 ", 1, 1)]
    public void a_shard_reads_as_i_of_n(string text, int index, int count)
    {
        RunShard.Parse(text, out var refusal).ShouldBe(new RunShard(index, count));
        refusal.ShouldBeNull();
    }

    [Theory]
    [InlineData("2", "is not a shard")]
    [InlineData("a/b", "is not a shard")]
    [InlineData("1/2/3", "is not a shard")]
    [InlineData("1/0", "the number of legs must be 1 or more")]
    [InlineData("0/2", "the leg must be between 1 and 2")]
    [InlineData("3/2", "the leg must be between 1 and 2")]
    public void a_malformed_shard_is_refused_by_name(string text, string message)
    {
        RunShard.Parse(text, out var refusal).ShouldBeNull();
        refusal!.ShouldContain(message);
    }

    [Fact]
    public void an_out_of_range_shard_is_refused_by_the_planner_too()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => WorkPlan.Shard(testsInClass("A", 1), new RunShard(3, 2)));
    }

    [Fact]
    public async Task the_supervisor_runs_only_its_leg_and_the_legs_together_run_everything()
    {
        // Three classes and an isolated test, which is a partition of its own: four partitions over
        // two legs, each in exactly one.
        var tests = new List<WorkerTest>
        {
            test("Basics.a"), test("Basics.b"), test("Fussy.a"), test("Fussy.b"), test("Lonely.a"),
            new("Isolated.alone", "Isolated.alone") { Traits = new Dictionary<string, string> { ["Isolated"] = "true" } }
        };

        var ran = new List<string>();
        foreach (var index in new[] { 1, 2 })
        {
            var factory = new FakeWorkerFactory { Tests = tests, Outcome = (_, _, _) => WorkerTestState.Passed };
            var results = await new Supervisor(factory) { Shard = new RunShard(index, 2) }.Run();

            var leg = results.ShardLeg.ShouldNotBeNull();
            leg.Shard.ShouldBe(new RunShard(index, 2));
            results.Tests.Count.ShouldBe(leg.Tests.Count);
            results.ExitCode.ShouldBe(0);
            ran.AddRange(results.Tests.Select(t => t.Uid));

            RunReport.ToText(results).ShouldContain($"Shard {index}/2:");
            var json = JsonNode.Parse(RunReport.ToJson(results))!;
            json["shard"]!["index"]!.GetValue<int>().ShouldBe(index);
            json["shard"]!["count"]!.GetValue<int>().ShouldBe(2);
            json["shard"]!["balancedBy"]!.GetValue<string>().ShouldBe("count");
            json["shard"]!["estimateMs"].ShouldBeNull();
        }

        ran.ShouldBe(tests.Select(t => t.Uid), ignoreOrder: true);
    }

    [Fact]
    public async Task an_empty_leg_passes_rather_than_aborting_as_a_stale_filter_would()
    {
        var factory = new FakeWorkerFactory { Tests = [test("Only.a")], Outcome = (_, _, _) => WorkerTestState.Passed };

        var results = await new Supervisor(factory) { Shard = new RunShard(2, 2), FailWhenNoTests = true }.Run();

        results.Tests.ShouldBeEmpty();
        results.AbortReason.ShouldBeNull();
        results.ExitCode.ShouldBe(0);
        results.ShardLeg!.Tests.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_unsharded_run_reports_no_shard()
    {
        var factory = new FakeWorkerFactory { Tests = [test("A.a")], Outcome = (_, _, _) => WorkerTestState.Passed };

        var results = await new Supervisor(factory).Run();

        results.ShardLeg.ShouldBeNull();
        RunReport.ToText(results).ShouldNotContain("Shard ");
        JsonNode.Parse(RunReport.ToJson(results))!["shard"].ShouldBeNull();
    }
}
