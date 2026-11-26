using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The gate's scheduler (bobcat#729) without a test host: tracks are exclusive, the budget is one
/// number across every unit, and the order is most work (wall clock times workers) first. Units complete only when the test
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

        public Task Run(IReadOnlyList<GateUnit> plan, int budget, CancellationToken? ct = null,
            Func<GateUnit, TimeSpan?>? predicted = null, TimeProvider? clock = null)
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
            }, ct ?? TestContext.Current.CancellationToken, predicted, clock);

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
    public void order_is_by_work_so_a_multi_worker_unit_goes_ahead_of_a_longer_single_worker_one()
    {
        var predicted = new Dictionary<string, TimeSpan>
        {
            ["Broker"] = TimeSpan.FromMinutes(10), // 10 worker-minutes
            ["Lanes"] = TimeSpan.FromMinutes(4),   // 4 workers: 16 worker-minutes
            ["Small"] = TimeSpan.FromMinutes(3)    // 2 workers: 6 worker-minutes
        };

        GateScheduler.Order([unit("Broker"), unit("Small", 2), unit("Lanes", 4)], u => predicted[u.Project.Name],
                budget: 9)
            .Select(u => u.Project.Name)
            .ShouldBe(["Lanes", "Broker", "Small"]);
    }

    [Fact]
    public void order_counts_the_workers_a_unit_gets_under_the_budget_not_the_ones_it_asks_for()
    {
        // Asks for 8 but gets 2: 2 x 4 = 8 worker-minutes, less than the single worker's 10
        var predicted = new Dictionary<string, TimeSpan>
        {
            ["Wide"] = TimeSpan.FromMinutes(4),
            ["Long"] = TimeSpan.FromMinutes(10)
        };

        GateScheduler.Order([unit("Wide", 8), unit("Long")], u => predicted[u.Project.Name], budget: 2)
            .Select(u => u.Project.Name)
            .ShouldBe(["Long", "Wide"]);
    }

    [Fact]
    public async Task ordered_by_work_a_multi_worker_unit_starts_while_the_budget_is_free()
    {
        // Two long single-worker units and a short four-lane one on a budget of four. By wall clock the
        // single-worker units go first and the four-lane unit waits for both to finish.
        var predicted = new Dictionary<string, TimeSpan>
        {
            ["Long1"] = TimeSpan.FromMinutes(10),
            ["Long2"] = TimeSpan.FromMinutes(9),
            ["Lanes"] = TimeSpan.FromMinutes(4)
        };
        var plan = GateScheduler.Order([unit("Long1"), unit("Long2"), unit("Lanes", 4)],
            u => predicted[u.Project.Name], budget: 4);

        var harness = new Harness();
        var run = harness.Run(plan, budget: 4);
        await Harness.settle();

        harness.Running.ShouldBe(["Lanes"]);

        await harness.Release("Lanes");
        harness.Running.ShouldBe(["Long1", "Long2"], ignoreOrder: true);

        await harness.Release("Long1");
        await harness.Release("Long2");
        await run;
        harness.Started.ShouldBe(["Lanes", "Long1", "Long2"]);
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task a_unit_held_back_by_workers_keeps_its_place_and_only_units_done_before_it_go_ahead()
    {
        var minutes = new Dictionary<string, TimeSpan?>
        {
            ["Big"] = TimeSpan.FromMinutes(4),
            ["Wide"] = TimeSpan.FromMinutes(2),
            ["Long"] = TimeSpan.FromMinutes(10),
            ["Short"] = TimeSpan.FromMinutes(1)
        };
        var clock = new ManualClock();
        var harness = new Harness();
        var run = harness.Run([unit("Big", 3), unit("Wide", 4), unit("Long"), unit("Short")], budget: 4,
            predicted: u => minutes[u.Project.Name], clock: clock);
        await Harness.settle();

        // Wide needs all four and reserves them for when Big is predicted to end (4 minutes). Short is
        // done by then and takes the spare worker; Long would still be running, so it waits.
        harness.Running.ShouldBe(["Big", "Short"], ignoreOrder: true);

        clock.Now += TimeSpan.FromMinutes(1);
        await harness.Release("Short");
        harness.Running.ShouldBe(["Big"]); // the spare worker is held for Wide, not given to Long

        clock.Now += TimeSpan.FromMinutes(3);
        await harness.Release("Big");
        harness.Running.ShouldBe(["Wide"]);

        await harness.Release("Wide");
        harness.Running.ShouldBe(["Long"]);

        await harness.Release("Long");
        await run;
        harness.Started.ShouldBe(["Big", "Short", "Wide", "Long"]);
    }

    [Fact]
    public async Task a_running_unit_past_its_prediction_still_holds_the_reservation()
    {
        var minutes = new Dictionary<string, TimeSpan?>
        {
            ["Big"] = TimeSpan.FromMinutes(4),
            ["Wide"] = TimeSpan.FromMinutes(2),
            ["Short"] = TimeSpan.FromMinutes(1)
        };
        var clock = new ManualClock();
        var harness = new Harness();
        var run = harness.Run([unit("Big", 3), unit("Wide", 4), unit("Short")], budget: 4,
            predicted: u => minutes[u.Project.Name], clock: clock);
        await Harness.settle();
        harness.Running.ShouldBe(["Big", "Short"], ignoreOrder: true);

        // Big is overdue: it is taken to end now, so nothing more fits before the reservation
        clock.Now += TimeSpan.FromMinutes(6);
        await harness.Release("Short");
        harness.Running.ShouldBe(["Big"]);

        await harness.Release("Big");
        await harness.Release("Wide");
        await run;
    }

    [Fact]
    public async Task without_a_prediction_for_a_running_unit_there_is_no_reservation()
    {
        var minutes = new Dictionary<string, TimeSpan?>
        {
            ["Big"] = null,
            ["Wide"] = TimeSpan.FromMinutes(2),
            ["Long"] = TimeSpan.FromMinutes(10)
        };
        var harness = new Harness();
        var run = harness.Run([unit("Big", 3), unit("Wide", 4), unit("Long")], budget: 4,
            predicted: u => minutes[u.Project.Name], clock: new ManualClock());
        await Harness.settle();

        // Nobody can say when Big frees its workers, so the spare one is used
        harness.Running.ShouldBe(["Big", "Long"], ignoreOrder: true);

        await harness.Release("Big");
        await harness.Release("Long");
        await harness.Release("Wide");
        await run;
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
