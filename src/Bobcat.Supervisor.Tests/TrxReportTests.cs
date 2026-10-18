using System.Xml.Linq;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>bobcat#699: a supervised run as a TRX file, for CI that reads one.</summary>
public class TrxReportTests
{
    private static readonly XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private static async Task<SupervisorResults> mixedRun()
    {
        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.Test("Specs.Orders.solid"),
                FakeWorkerFactory.Test("Specs.Orders.flaky", "Retry=2"),
                FakeWorkerFactory.Test("Specs.Billing.broken"),
                FakeWorkerFactory.Test("Specs.Billing.ignored")
            ],
            Outcome = (uid, attempt, _) => uid switch
            {
                "Specs.Orders.solid" => WorkerTestState.Passed,
                "Specs.Orders.flaky" => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed,
                "Specs.Billing.ignored" => WorkerTestState.Skipped,
                _ => WorkerTestState.Error
            },
            ErrorType = (_, _) => "System.TimeoutException",
            Duration = (_, attempt) => TimeSpan.FromMilliseconds(100 * attempt)
        };

        return await new Supervisor(factory) { RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 } }.Run();
    }

    private static XElement resultFor(XDocument trx, string name)
        => trx.Root!.Element(ns + "Results")!.Elements(ns + "UnitTestResult")
            .Single(r => (string)r.Attribute("testName")! == name);

    [Fact]
    public async Task one_result_per_test_with_counters_a_reporter_reads()
    {
        var results = await mixedRun();
        var trx = XDocument.Parse(RunReport.ToTrx(results));

        trx.Root!.Name.ShouldBe(ns + "TestRun");
        trx.Root.Element(ns + "Results")!.Elements(ns + "UnitTestResult").Count().ShouldBe(4);
        trx.Root.Element(ns + "TestDefinitions")!.Elements(ns + "UnitTest").Count().ShouldBe(4);

        var summary = trx.Root.Element(ns + "ResultSummary")!;
        summary.Attribute("outcome")!.Value.ShouldBe("Failed");

        var counters = summary.Element(ns + "Counters")!;
        counters.Attribute("total")!.Value.ShouldBe("4");
        counters.Attribute("executed")!.Value.ShouldBe("3");
        counters.Attribute("passed")!.Value.ShouldBe("2");
        counters.Attribute("failed")!.Value.ShouldBe("1");
        counters.Attribute("notExecuted")!.Value.ShouldBe("1");

        resultFor(trx, "Specs.Billing.ignored").Attribute("outcome")!.Value.ShouldBe("NotExecuted");
    }

    [Fact]
    public async Task a_pass_on_retry_says_so_and_carries_every_attempt()
    {
        var trx = XDocument.Parse(RunReport.ToTrx(await mixedRun()));
        var flaky = resultFor(trx, "Specs.Orders.flaky");

        flaky.Attribute("outcome")!.Value.ShouldBe("Passed");
        flaky.Element(ns + "Output")!.Element(ns + "StdOut")!.Value.ShouldStartWith("PASSED ON RETRY");

        // The time the test cost the run is both attempts', not just the one that passed.
        flaky.Attribute("duration")!.Value.ShouldBe("00:00:00.3000000");

        var attempts = flaky.Element(ns + "InnerResults")!.Elements(ns + "UnitTestResult").ToList();
        attempts.Select(a => a.Attribute("outcome")!.Value).ShouldBe(["Failed", "Passed"]);
        attempts.ShouldAllBe(a => a.Attribute("parentExecutionId")!.Value == flaky.Attribute("executionId")!.Value);
        attempts[0].Descendants(ns + "Message").Single().Value.ShouldContain("System.TimeoutException");
        attempts[0].Attribute("duration")!.Value.ShouldBe("00:00:00.1000000");

        // And the run says it out loud too.
        trx.Root!.Descendants(ns + "RunInfo")
            .ShouldContain(info => info.Value.Contains("passed on retry") && info.Value.Contains("Specs.Orders.flaky"));
    }

    [Fact]
    public async Task a_clean_pass_has_no_inner_results_and_tests_group_by_class()
    {
        var trx = XDocument.Parse(RunReport.ToTrx(await mixedRun()));

        resultFor(trx, "Specs.Orders.solid").Element(ns + "InnerResults").ShouldBeNull();

        var classes = trx.Root!.Descendants(ns + "TestMethod")
            .ToDictionary(m => m.Attribute("name")!.Value, m => m.Attribute("className")!.Value);
        classes["Specs.Orders.solid"].ShouldBe("Specs.Orders");
        classes["Specs.Billing.broken"].ShouldBe("Specs.Billing");
    }

    [Fact]
    public void a_failure_carries_its_message_and_stack_trace()
    {
        var results = resultsOf(test("Ns.Specs.boom", attempt(1, new WorkerOutcome("boom", "Ns.Specs.boom", WorkerTestState.Failed)
        {
            ErrorType = "Shouldly.ShouldAssertException",
            ErrorMessage = "Shouldly.ShouldAssertException : total should be 3 but was 4",
            StackTrace = "   at Ns.Specs.boom() in Specs.cs:line 12",
            TestClass = "Ns.Specs"
        }, Disposition.FailAndContinue("no retry"))));

        var trx = XDocument.Parse(RunReport.ToTrx(results));
        var error = resultFor(trx, "Ns.Specs.boom").Element(ns + "Output")!.Element(ns + "ErrorInfo")!;

        // The type is said once, as xUnit already wrote it.
        error.Element(ns + "Message")!.Value.ShouldBe("Shouldly.ShouldAssertException : total should be 3 but was 4");
        error.Element(ns + "StackTrace")!.Value.ShouldContain("Specs.cs:line 12");
    }

    [Fact]
    public void an_indeterminate_test_is_a_failure_that_says_what_it_is()
    {
        var results = resultsOf(test("Ns.Specs.lost",
            attempt(1, new WorkerOutcome("lost", "Ns.Specs.lost", WorkerTestState.Indeterminate)
            {
                ErrorMessage = "worker exited with code 134"
            }, Disposition.FailAndContinue("worker died"))), faults: ["exit 134: Fatal error"]);

        var trx = XDocument.Parse(RunReport.ToTrx(results));
        var lost = resultFor(trx, "Ns.Specs.lost");

        lost.Attribute("outcome")!.Value.ShouldBe("Failed");
        var message = lost.Descendants(ns + "Message").Single().Value;
        message.ShouldStartWith("INDETERMINATE");
        message.ShouldContain("worker exited with code 134");

        trx.Root!.Descendants(ns + "RunInfo").ShouldContain(info => info.Value.Contains("exit 134: Fatal error"));
    }

    [Fact]
    public void an_aborted_run_is_aborted_in_the_summary()
    {
        var results = resultsOf(test("Ns.Specs.down",
            attempt(1, new WorkerOutcome("down", "Ns.Specs.down", WorkerTestState.Failed) { ErrorMessage = "db gone" },
                Disposition.AbortRun("catastrophic"))), abortReason: "the database is gone");

        var trx = XDocument.Parse(RunReport.ToTrx(results));

        trx.Root!.Element(ns + "ResultSummary")!.Attribute("outcome")!.Value.ShouldBe("Aborted");
        resultFor(trx, "Ns.Specs.down").Descendants(ns + "Message").Single().Value.ShouldStartWith("RUN ABORTED");
        trx.Root.Descendants(ns + "RunInfo").ShouldContain(info => info.Value.Contains("the database is gone"));
    }

    [Fact]
    public async Task the_options_supply_what_the_results_do_not_carry_and_ids_are_stable()
    {
        var results = await mixedRun();
        var started = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
        var options = new TrxOptions { StartedAt = started, Storage = "/out/Specs.dll", RunName = "nightly", ComputerName = "ci-1" };

        var first = XDocument.Parse(RunReport.ToTrx(results, options));
        var second = XDocument.Parse(RunReport.ToTrx(results, options));

        first.Root!.Attribute("name")!.Value.ShouldBe("nightly");
        first.Root.Element(ns + "Times")!.Attribute("start")!.Value.ShouldStartWith("2026-10-17T09:00:00");
        first.Root.Descendants(ns + "UnitTest").ShouldAllBe(t => t.Attribute("storage")!.Value == "/out/Specs.dll");
        first.Root.Descendants(ns + "UnitTestResult").ShouldAllBe(r => r.Attribute("computerName")!.Value == "ci-1");

        ids(first).ShouldBe(ids(second));
    }

    [Fact]
    public async Task write_trx_creates_the_directory_and_writes_a_readable_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "bobcat-trx-" + Guid.NewGuid(), "nested", "run.trx");
        try
        {
            RunReport.WriteTrx(await mixedRun(), path);

            var trx = XDocument.Load(path);
            trx.Root!.Element(ns + "Results")!.Elements(ns + "UnitTestResult").Count().ShouldBe(4);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
        }
    }

    private static string[] ids(XDocument trx)
        => trx.Root!.Descendants(ns + "UnitTestResult").Select(r => r.Attribute("testId")!.Value).ToArray();

    private static SupervisorAttempt attempt(int number, WorkerOutcome outcome, Disposition disposition)
        => new(number, outcome, AttemptPlacement.Batched, disposition);

    private static TestReport test(string name, params SupervisorAttempt[] attempts)
        => new() { Uid = attempts[0].Outcome.Uid, DisplayName = name, Attempts = attempts };

    private static SupervisorResults resultsOf(TestReport test, string? abortReason = null, string[]? faults = null)
        => new() { Tests = [test], AbortReason = abortReason, WorkerFaults = faults ?? [] };
}
