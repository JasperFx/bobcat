using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The gate's scheduler (bobcat#729) without a test host: tracks are exclusive, the budget is one
/// number across every unit, and the order is longest first. Units complete only when the test
/// says so, so overlap is proved rather than timed.
/// </summary>
public class GateSchedulerTests
{
    private static readonly Track postgres = new("postgres");
    private static readonly Track sqlserver = new("sqlserver");

    private static GateUnit unit(string name, int workers = 1, params Track[] tracks)
        => new(new GateProject(Path.Combine(Path.GetTempPath(), name, name + ".csproj"), workers), "net10.0", tracks, workers, name);

    /// <summary>A scheduler run whose units finish when the test releases them.</summary>
    private sealed class Harness
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private readonly List<string> _started = [];
        private readonly HashSet<string> _running = [];
        private readonly Dictionary<string, int> _workers = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public Task Run(IReadOnlyList<GateUnit> plan, int budget, CancellationToken? ct = null)
            => GateScheduler.Run(plan, budget, async (u, workers, _) =>
            {
                TaskCompletionSource gate;
                lock (_lock)
                {
                    _started.Add(u.Project.Name);
                    _running.Add(u.Project.Name);
                    _workers[u.Project.Name] = workers;
                    gate = _gates[u.Project.Name] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                try
                {
                    await gate.Task;
                }
                finally
                {
                    lock (_lock) _running.Remove(u.Project.Name);
                }
            }, ct ?? TestContext.Current.CancellationToken);

        public IReadOnlyList<string> Started { get { lock (_lock) return _started.ToArray(); } }
        public IReadOnlySet<string> Running { get { lock (_lock) return _running.ToHashSet(); } }
        public int WorkersGiven(string name) { lock (_lock) return _workers[name]; }

        /// <summary>Let a unit finish, and give the scheduler a moment to react.</summary>
        public async Task Release(string name)
        {
            TaskCompletionSource gate;
            lock (_lock) gate = _gates[name];
            gate.SetResult();
            await settle();
        }

        public async Task Fail(string name, Exception e)
        {
            TaskCompletionSource gate;
            lock (_lock) gate = _gates[name];
            gate.SetException(e);
            await settle();
        }

        /// <summary>Yield until the scheduler has had a chance to start whatever now fits.</summary>
        public static async Task settle()
        {
            for (var i = 0; i < 20; i++) await Task.Yield();
            await Task.Delay(20);
        }
    }

    [Fact]
    public void order_puts_unknown_units_first_in_declaration_order_then_longest_first()
    {
        var a = unit("A");
        var b = unit("B");
        var c = unit("C");
        var d = unit("D");

        var predicted = new Dictionary<string, TimeSpan?>
        {
            ["A"] = TimeSpan.FromMinutes(2),
            ["B"] = null,
            ["C"] = TimeSpan.FromMinutes(10),
            ["D"] = null
        };

        GateScheduler.Order([a, b, c, d], u => predicted[u.Project.Name]).Select(u => u.Project.Name)
            .ShouldBe(["B", "D", "C", "A"]);
    }

    [Fact]
    public void order_is_declaration_order_when_nothing_is_known()
    {
        GateScheduler.Order([unit("Z"), unit("Y"), unit("X")], _ => null).Select(u => u.Project.Name)
            .ShouldBe(["Z", "Y", "X"]);
    }

    [Fact]
    public async Task two_units_on_one_track_never_overlap()
    {
        var harness = new Harness();
        var run = harness.Run([unit("A", 1, postgres), unit("B", 1, postgres)], budget: 8);
        await Harness.settle();

        harness.Running.ShouldBe(["A"]);

        await harness.Release("A");
        harness.Running.ShouldBe(["B"]);

        await harness.Release("B");
        await run;
        harness.Started.ShouldBe(["A", "B"]);
    }

    [Fact]
    public async Task tracks_run_concurrently_with_each_other()
    {
        var harness = new Harness();
        var run = harness.Run([unit("A", 1, postgres), unit("B", 1, sqlserver), unit("C")], budget: 8);
        await Harness.settle();

        harness.Running.ShouldBe(["A", "B", "C"], ignoreOrder: true);

        await harness.Release("A");
        await harness.Release("B");
        await harness.Release("C");
        await run;
    }

    [Fact]
    public async Task a_unit_holding_several_tracks_waits_for_all_of_them()
    {
        var harness = new Harness();
        var run = harness.Run(
            [unit("A", 1, postgres), unit("B", 1, sqlserver), unit("EF", 1, postgres, sqlserver)], budget: 8);
        await Harness.settle();

        harness.Running.ShouldBe(["A", "B"], ignoreOrder: true);

        await harness.Release("A");
        harness.Running.ShouldBe(["B"]);   // EF still waits: sqlserver is held

        await harness.Release("B");
        harness.Running.ShouldBe(["EF"]);

        await harness.Release("EF");
        await run;
    }

    [Fact]
    public async Task a_unit_holding_several_tracks_blocks_both_of_them_while_it_runs()
    {
        var harness = new Harness();
        var run = harness.Run(
            [unit("EF", 1, postgres, sqlserver), unit("A", 1, postgres), unit("B", 1, sqlserver)], budget: 8);
        await Harness.settle();

        harness.Running.ShouldBe(["EF"]);

        await harness.Release("EF");
        harness.Running.ShouldBe(["A", "B"], ignoreOrder: true);

        await harness.Release("A");
        await harness.Release("B");
        await run;
    }

    [Fact]
    public async Task the_budget_is_one_number_across_every_unit_and_a_smaller_unit_fills_the_gap()
    {
        var harness = new Harness();
        var run = harness.Run([unit("A", 3), unit("B", 2), unit("C", 1)], budget: 4);
        await Harness.settle();

        // A takes 3 of 4; B (2) does not fit and is stepped over; C (1) does.
        harness.Running.ShouldBe(["A", "C"], ignoreOrder: true);

        await harness.Release("C");
        harness.Running.ShouldBe(["A"]);   // B still does not fit beside A

        await harness.Release("A");
        harness.Running.ShouldBe(["B"]);

        await harness.Release("B");
        await run;
        harness.Started.ShouldBe(["A", "C", "B"]);
    }

    [Fact]
    public async Task a_unit_asking_for_more_than_the_budget_is_clamped_not_held_back()
    {
        var harness = new Harness();
        var run = harness.Run([unit("Big", 8)], budget: 2);
        await Harness.settle();

        harness.Running.ShouldBe(["Big"]);
        harness.WorkersGiven("Big").ShouldBe(2);

        await harness.Release("Big");
        await run;
    }

    [Fact]
    public void workers_for_is_the_declared_count_within_the_budget()
    {
        GateScheduler.WorkersFor(unit("A", 4), 8).ShouldBe(4);
        GateScheduler.WorkersFor(unit("A", 4), 2).ShouldBe(2);
        GateScheduler.WorkersFor(unit("A", 1), 0).ShouldBe(1);
    }

    [Fact]
    public async Task a_throwing_unit_stops_new_starts_and_is_rethrown_after_the_running_ones_return()
    {
        var harness = new Harness();
        var run = harness.Run([unit("A", 1, postgres), unit("B", 1, sqlserver), unit("C", 1, postgres)], budget: 8);
        await Harness.settle();
        harness.Running.ShouldBe(["A", "B"], ignoreOrder: true);

        await harness.Fail("A", new InvalidOperationException("host would not start"));

        // C's track is free now, but nothing new starts after a fault; B is still running and the
        // scheduler has not returned.
        harness.Running.ShouldBe(["B"]);
        run.IsCompleted.ShouldBeFalse();

        await harness.Release("B");
        var thrown = await Should.ThrowAsync<InvalidOperationException>(run);
        thrown.Message.ShouldBe("host would not start");
        harness.Started.ShouldNotContain("C");
    }

    [Fact]
    public async Task cancellation_stops_new_starts_and_surfaces_once_the_running_unit_returns()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = new Harness();
        var run = harness.Run([unit("A", 1, postgres), unit("B", 1, postgres)], budget: 8, cancellation.Token);
        await Harness.settle();
        harness.Running.ShouldBe(["A"]);

        cancellation.Cancel();
        await harness.Release("A");

        await Should.ThrowAsync<OperationCanceledException>(run);
        harness.Started.ShouldBe(["A"]);
    }

    [Fact]
    public async Task an_empty_plan_completes()
    {
        await GateScheduler.Run([], 4, (_, _, _) => throw new InvalidOperationException("never called"),
            TestContext.Current.CancellationToken);
    }
}
