using System.Text.Json;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#663: when <see cref="Supervisor.KnownTestDurations"/> matches none of the discovered
/// tests (a ledger recorded on another machine, configuration or TFM, because xUnit v3's uid
/// hashes the assembly path), the plan falls back to counting tests — and must say so.
/// </summary>
public class DurationCoverageTests
{
    private sealed class PlanObserver : ISupervisorObserver
    {
        public List<(IReadOnlyList<WorkLane> Lanes, DurationCoverage? Coverage)> Plans { get; } = [];

        public void LanesPlanned(IReadOnlyList<WorkLane> lanes, DurationCoverage? coverage)
            => Plans.Add((lanes, coverage));
    }

    private static FakeWorkerFactory fourTests() => new()
    {
        Tests =
        [
            FakeWorkerFactory.InClass("A", "one"), FakeWorkerFactory.InClass("A", "two"),
            FakeWorkerFactory.InClass("B", "one"), FakeWorkerFactory.InClass("B", "two")
        ],
        Outcome = (_, _, _) => WorkerTestState.Passed
    };

    private static async Task<(SupervisorResults Results, PlanObserver Observer, List<string> Log)> run(
        IReadOnlyDictionary<string, TimeSpan>? known)
    {
        var observer = new PlanObserver();
        var log = new List<string>();
        var supervisor = new Supervisor(fourTests())
        {
            MaxParallelWorkers = 2,
            KnownTestDurations = known,
            Log = line => { lock (log) log.Add(line); }
        };
        supervisor.AddObserver(observer);

        return (await supervisor.Run(), observer, log);
    }

    [Fact]
    public async Task a_ledger_that_matches_nothing_says_the_lanes_were_balanced_by_count()
    {
        // Durations keyed by another machine's uids: every one is valid, none is this run's.
        var foreign = new Dictionary<string, TimeSpan>
        {
            ["a1b2c3"] = TimeSpan.FromSeconds(30),
            ["d4e5f6"] = TimeSpan.FromSeconds(2)
        };

        var (results, observer, log) = await run(foreign);

        results.DurationCoverage.ShouldBe(new DurationCoverage(0, 4));
        results.DurationCoverage!.BalancedByCount.ShouldBeTrue();

        const string said = "Known durations matched 0/4 test(s): lanes balanced by test count";
        RunReport.ToText(results).ShouldContain(said);
        log.ShouldContain(said);
        observer.Plans.ShouldHaveSingleItem().Coverage.ShouldBe(new DurationCoverage(0, 4));

        var json = JsonDocument.Parse(RunReport.ToJson(results)).RootElement.GetProperty("knownDurations");
        json.GetProperty("matched").GetInt32().ShouldBe(0);
        json.GetProperty("tests").GetInt32().ShouldBe(4);
        json.GetProperty("balancedBy").GetString().ShouldBe("count");
    }

    [Fact]
    public async Task a_partial_match_reports_how_many_matched()
    {
        var known = new Dictionary<string, TimeSpan>
        {
            ["A.one"] = TimeSpan.FromSeconds(30),
            ["A.two"] = TimeSpan.FromSeconds(30),
            ["B.one"] = TimeSpan.FromSeconds(1)
        };

        var (results, _, _) = await run(known);

        results.DurationCoverage.ShouldBe(new DurationCoverage(3, 4));
        RunReport.ToText(results).ShouldContain("Known durations matched 3/4 test(s)");
        JsonDocument.Parse(RunReport.ToJson(results)).RootElement
            .GetProperty("knownDurations").GetProperty("balancedBy").GetString().ShouldBe("duration");
    }

    [Fact]
    public async Task no_durations_supplied_is_a_deliberate_count_balance_and_says_nothing()
    {
        var (results, observer, _) = await run(null);

        results.DurationCoverage.ShouldBeNull();
        RunReport.ToText(results).ShouldNotContain("Known durations");
        JsonDocument.Parse(RunReport.ToJson(results)).RootElement
            .GetProperty("knownDurations").ValueKind.ShouldBe(JsonValueKind.Null);
        observer.Plans.ShouldHaveSingleItem().Coverage.ShouldBeNull();
    }
}
