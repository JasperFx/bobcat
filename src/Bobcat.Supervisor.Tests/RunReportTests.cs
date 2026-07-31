using JasperFx.Testing;
using System.Text.Json;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

public class RunReportTests
{
    private static async Task<SupervisorResults> run(
        IReadOnlyList<WorkerTest> tests,
        Func<string, int, FakeWorker, WorkerTestState?> outcome,
        RetryBudget? budget = null)
    {
        var factory = new FakeWorkerFactory { Tests = tests, Outcome = outcome };
        return await new Supervisor(factory) { RetryBudget = budget ?? RetryBudget.None }.Run();
    }

    private static Task<SupervisorResults> mixedRun() => run(
        [
            FakeWorkerFactory.Test("solid"),
            FakeWorkerFactory.Test("flaky", "Retry=2"),
            FakeWorkerFactory.Test("broken")
        ],
        (uid, attempt, _) => uid switch
        {
            "solid" => WorkerTestState.Passed,
            "flaky" => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed,
            _ => WorkerTestState.Error
        },
        new RetryBudget { MaxAttemptsPerTest = 2 });

    [Fact]
    public async Task the_human_report_separates_passed_on_retry_from_clean_passes()
    {
        var text = RunReport.ToText(await mixedRun());

        text.ShouldContain("Passed on retry (not clean passes)");
        text.ShouldContain("flaky");
        text.ShouldContain("2 attempts");

        // The clean pass is counted, never listed among the retries.
        text.ShouldContain("1 passed");
        text.Split("Passed on retry")[1].ShouldNotContain("solid");
    }

    [Fact]
    public async Task the_human_report_lists_quarantine_candidates()
    {
        var text = RunReport.ToText(await mixedRun());

        text.ShouldContain("Quarantine candidates");
        text.ShouldContain("flaky");
    }

    [Fact]
    public async Task a_test_that_passed_on_retry_is_a_quarantine_candidate_despite_the_green_build()
    {
        // Membership is "was retried", not "eventually failed" — a green build is exactly when
        // this would otherwise go unnoticed.
        var results = await mixedRun();

        results.Quarantine.Select(t => t.Uid).ShouldBe(["flaky"]);
        results.PassedOnRetry.ShouldHaveSingleItem().Uid.ShouldBe("flaky");
    }

    [Fact]
    public async Task the_json_report_is_structured_rather_than_scraped()
    {
        var json = JsonDocument.Parse(RunReport.ToJson(await mixedRun())).RootElement;

        var summary = json.GetProperty("summary");
        summary.GetProperty("cleanPass").GetInt32().ShouldBe(1);
        summary.GetProperty("passOnRetry").GetInt32().ShouldBe(1);
        summary.GetProperty("failed").GetInt32().ShouldBe(1);
        summary.GetProperty("retriesPerformed").GetInt32().ShouldBe(1);

        json.GetProperty("exitCode").GetInt32().ShouldBe(1);
        json.GetProperty("quarantine").EnumerateArray().Select(e => e.GetString()).ShouldBe(["flaky"]);
    }

    [Fact]
    public async Task each_attempt_carries_its_placement_and_disposition()
    {
        // An agent asking "which tests are flaky and under what conditions" reads fields, not a
        // console log.
        var json = JsonDocument.Parse(RunReport.ToJson(await mixedRun())).RootElement;

        var flaky = json.GetProperty("tests").EnumerateArray()
            .Single(t => t.GetProperty("uid").GetString() == "flaky");

        flaky.GetProperty("outcome").GetString().ShouldBe(nameof(RunOutcome.PassOnRetry));
        flaky.GetProperty("quarantineCandidate").GetBoolean().ShouldBeTrue();

        var attempts = flaky.GetProperty("attemptDetail").EnumerateArray().ToList();
        attempts.Count.ShouldBe(2);
        attempts[0].GetProperty("placement").GetString().ShouldBe(nameof(AttemptPlacement.Batched));
        attempts[0].GetProperty("disposition").GetString().ShouldBe(nameof(DispositionKind.RetryInProcess));
        attempts[1].GetProperty("placement").GetString().ShouldBe(nameof(AttemptPlacement.SameProcess));
    }

    [Fact]
    public async Task a_recycled_retry_names_the_resources_in_the_json()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("brokered", "RecycleOnRetry=rabbit,kafka", "Retry=2")],
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var supervisor = new Supervisor(factory) { RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 } };
        supervisor.AddRecyclableResource(new StubRecyclable("rabbit"));
        supervisor.AddRecyclableResource(new StubRecyclable("kafka"));

        var json = JsonDocument.Parse(RunReport.ToJson(await supervisor.Run())).RootElement;

        json.GetProperty("recyclings").EnumerateArray()
            .Select(e => e.GetString()).ShouldBe(["rabbit", "kafka"]);

        var first = json.GetProperty("tests").EnumerateArray().Single()
            .GetProperty("attemptDetail").EnumerateArray().First();

        first.GetProperty("recycled").EnumerateArray()
            .Select(e => e.GetString()).ShouldBe(["rabbit", "kafka"]);
    }

    [Fact]
    public async Task an_aborted_run_leads_with_the_reason()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory);
        supervisor.Preflight.Add("docker is running", () => throw new InvalidOperationException("connection refused"));

        var results = await supervisor.Run();

        RunReport.ToText(results).ShouldStartWith("RUN ABORTED");
        JsonDocument.Parse(RunReport.ToJson(results)).RootElement
            .GetProperty("abortReason").GetString().ShouldContain("connection refused");
    }

    [Fact]
    public async Task a_clean_run_reports_no_sections_it_does_not_need()
    {
        var results = await run([FakeWorkerFactory.Test("a")], (_, _, _) => WorkerTestState.Passed);

        var text = RunReport.ToText(results);

        text.ShouldNotContain("Passed on retry");
        text.ShouldNotContain("Quarantine");
        text.ShouldNotContain("Failed:");
        text.ShouldContain("1 passed");
    }

    private sealed class StubRecyclable(string name) : Bobcat.Runtime.IRecyclableResource
    {
        public string Name { get; } = name;
        public Task Recycle(CancellationToken token = default) => Task.CompletedTask;
        public Task Start() => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }
}
