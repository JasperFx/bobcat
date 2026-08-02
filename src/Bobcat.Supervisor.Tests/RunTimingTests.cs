using System.Text.Json;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #56 layer 1 — where a run spent its time. The facts are reported; nothing acts on them.
/// </summary>
public class RunTimingTests
{
    /// <summary>
    /// The shape that motivated the issue: 78 tests where one <c>try_it_out</c> with a
    /// one-minute <c>Task.Delay</c> and no assertions was 35% of the run.
    /// </summary>
    private static async Task<SupervisorResults> lopsidedRun()
    {
        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.InClass("Bugs", "try_it_out"),
                FakeWorkerFactory.InClass("Orders", "places_an_order"),
                FakeWorkerFactory.InClass("Orders", "cancels_an_order")
            ],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            Duration = (uid, _) => uid.Contains("try_it_out")
                ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromSeconds(2)
        };

        return await new Supervisor(factory).Run();
    }

    [Fact]
    public async Task the_run_reports_its_own_wall_clock()
    {
        var results = await lopsidedRun();

        // Stamped by the supervisor, so it covers discovery and the gaps too — not just the
        // durations the tests happened to report.
        results.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
        results.WorkerLaunchTime.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task the_slowest_test_is_named_with_its_share_of_the_run()
    {
        var timing = RunTiming.For(await lopsidedRun());

        var slowest = timing.Slowest(5);
        slowest[0].DisplayName.ShouldBe("Bugs.try_it_out");
        slowest[0].Total.ShouldBe(TimeSpan.FromSeconds(60));

        // 60 of 64 measured seconds. Wall clock here is the fake's, which is near-instant, so
        // the share is computed against it rather than asserted as a percentage.
        timing.Measured.ShouldBe(TimeSpan.FromSeconds(64));
        timing.Concentration(1).ShouldBe(timing.Share(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task the_human_report_leads_with_the_percentage_not_the_seconds()
    {
        // "60.9s" reads as "integration tests are slow". "35% of wall clock" reads as something
        // to go and look at.
        var text = RunReport.ToText(await lopsidedRun());

        text.ShouldContain("Timing (wall clock");
        text.ShouldContain("the slowest test is");
        text.ShouldContain("% of wall clock");
        text.ShouldContain("Slowest:");
        text.ShouldContain("Bugs.try_it_out");
        text.ShouldContain("parallel efficiency");
    }

    [Fact]
    public async Task retries_are_charged_to_the_test_that_needed_them()
    {
        // Per-process profiling never sees this: each attempt looks like an ordinary run of an
        // ordinary test, and only the attempt history knows a 4s test cost the run 12s.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=3"), FakeWorkerFactory.Test("solid")],
            Outcome = (uid, attempt, _) =>
                uid == "solid" || attempt >= 3 ? WorkerTestState.Passed : WorkerTestState.Failed,
            Duration = (_, _) => TimeSpan.FromSeconds(4)
        };

        var results = await new Supervisor(factory)
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 }
        }.Run();

        var timing = RunTiming.For(results);

        var flaky = timing.Tests.Single(t => t.Uid == "flaky");
        flaky.Total.ShouldBe(TimeSpan.FromSeconds(12));
        flaky.FirstAttempt.ShouldBe(TimeSpan.FromSeconds(4));
        flaky.RetryCost.ShouldBe(TimeSpan.FromSeconds(8));

        // Run-wide, only the retried test contributes.
        timing.RetryCost.ShouldBe(TimeSpan.FromSeconds(8));
        RunReport.ToText(results).ShouldContain("of the run was retries");
    }

    [Fact]
    public async Task running_a_test_alone_is_reported_as_a_price_rather_than_folded_into_the_total()
    {
        // Isolation buys reliability by spending wall clock. That trade should be a number.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("lonely", "Isolated=true"), FakeWorkerFactory.Test("shared")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            Duration = (_, _) => TimeSpan.FromSeconds(3)
        };

        var results = await new Supervisor(factory).Run();
        var timing = RunTiming.For(results);

        timing.IsolationCost.ShouldBe(TimeSpan.FromSeconds(3));
        RunReport.ToText(results).ShouldContain("the price of isolation");
    }

    [Fact]
    public async Task a_test_that_reported_no_duration_is_counted_not_zero_filled()
    {
        // A test nobody measured is not a test that took no time. Averaging the difference away
        // would make every other figure quietly wrong.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("timed"), FakeWorkerFactory.Test("untimed")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            Duration = (uid, _) => uid == "timed" ? TimeSpan.FromSeconds(5) : null
        };

        var results = await new Supervisor(factory).Run();
        var timing = RunTiming.For(results);

        timing.Unmeasured.ShouldBe(1);
        timing.Tests.ShouldHaveSingleItem().Uid.ShouldBe("timed");
        timing.Measured.ShouldBe(TimeSpan.FromSeconds(5));

        RunReport.ToText(results).ShouldContain("are a floor, not a total");
    }

    [Fact]
    public async Task a_run_nobody_timed_says_so_instead_of_reporting_zeroes()
    {
        // tUnit erases durations on the MTP wire the same way it erases exception types. An
        // empty timing section would read as "the run was instant".
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var text = RunReport.ToText(await new Supervisor(factory).Run());

        text.ShouldContain("no test reported a duration");
        text.ShouldNotContain("parallel efficiency");
    }

    [Fact]
    public async Task the_json_timing_block_distinguishes_unmeasured_from_zero()
    {
        // An agent reading this has to be able to tell "no time was spent" from "nobody measured".
        var measured = JsonDocument.Parse(RunReport.ToJson(await lopsidedRun()))
            .RootElement.GetProperty("timing");

        measured.GetProperty("measuredMs").GetDouble().ShouldBe(64_000);
        measured.GetProperty("parallelEfficiency").GetDouble().ShouldBeGreaterThan(0);
        measured.GetProperty("concentration").GetProperty("slowest").GetDouble().ShouldBeGreaterThan(0);

        var slowest = measured.GetProperty("slowest").EnumerateArray().First();
        slowest.GetProperty("displayName").GetString().ShouldBe("Bugs.try_it_out");
        slowest.GetProperty("totalMs").GetDouble().ShouldBe(60_000);
        slowest.GetProperty("attempts").GetInt32().ShouldBe(1);

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var unmeasured = JsonDocument.Parse(RunReport.ToJson(await new Supervisor(factory).Run()))
            .RootElement.GetProperty("timing");

        unmeasured.GetProperty("measuredMs").ValueKind.ShouldBe(JsonValueKind.Null);
        unmeasured.GetProperty("parallelEfficiency").ValueKind.ShouldBe(JsonValueKind.Null);
        unmeasured.GetProperty("concentration").ValueKind.ShouldBe(JsonValueKind.Null);
        unmeasured.GetProperty("wallClockMs").GetDouble().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void results_nobody_timed_get_no_timing_section_at_all()
    {
        // Hand-built results, or a caller that never went through Supervisor.Run. Printing
        // zeroes as though they were measurements would be worse than saying nothing.
        var results = new SupervisorResults { Tests = [] };

        RunReport.ToText(results).ShouldNotContain("Timing");
    }
}
