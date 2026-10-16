using System.Text.Json;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#662: what a pass-on-retry's failed attempt failed WITH decides whether the test belongs
/// on retries or is a real bug, so the reports carry it and the retry's log line names the test.
/// </summary>
public class RecoveredFailureReportTests
{
    // The uid is a hash, as xUnit's is; only the display name says which test it is.
    private static readonly WorkerTest flaky = new("9f2c41aa", "Orders.Specs.flaky_broker")
    {
        Traits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Retry"] = "2" }
    };

    private static async Task<(SupervisorResults Results, List<string> Log)> run(string message)
    {
        var log = new List<string>();
        var factory = new FakeWorkerFactory
        {
            Tests = [flaky],
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Error,
            ErrorType = (_, _) => "System.TimeoutException",
            ErrorMessageFor = (_, _) => message
        };

        var results = await new Supervisor(factory)
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 },
            Log = log.Add
        }.Run();

        return (results, log);
    }

    [Fact]
    public async Task the_text_report_says_what_the_first_attempt_failed_with()
    {
        var (results, _) = await run("System.TimeoutException : the broker did not answer in 5s");

        var text = RunReport.ToText(results);
        var line = text.Split("Passed on retry")[1].Split('\n').First(l => l.Contains("flaky_broker"));

        // The type once, not twice: xUnit writes it at the head of the message.
        line.ShouldContain("first failed with System.TimeoutException: the broker did not answer in 5s (attempt 1)");
    }

    [Fact]
    public async Task the_json_report_carries_the_failure_a_pass_on_retry_recovered_from()
    {
        var (results, _) = await run("System.TimeoutException : the broker did not answer in 5s");

        var test = JsonDocument.Parse(RunReport.ToJson(results)).RootElement
            .GetProperty("tests").EnumerateArray().Single();

        test.GetProperty("outcome").GetString().ShouldBe(nameof(RunOutcome.PassOnRetry));
        var recovered = test.GetProperty("recoveredFrom");
        recovered.GetProperty("attempt").GetInt32().ShouldBe(1);
        recovered.GetProperty("errorType").GetString().ShouldBe("System.TimeoutException");
        recovered.GetProperty("message").GetString().ShouldBe("the broker did not answer in 5s");
    }

    [Fact]
    public async Task the_message_is_bounded_to_its_first_line_and_a_fixed_length()
    {
        var (results, _) = await run(
            "System.TimeoutException : " + new string('x', 500) + "\n   at Orders.Broker.Send()");

        var message = RunReport.RecoveredFrom(results.PassedOnRetry.Single())!.Message!;

        message.Length.ShouldBe(RunReport.FailureMessageLimit + 1);
        message.ShouldEndWith("…");
        message.ShouldNotContain("Orders.Broker.Send");
    }

    [Fact]
    public async Task a_retry_log_line_names_the_test_and_the_failure_not_the_uid_hash()
    {
        var (_, log) = await run("System.TimeoutException : the broker did not answer in 5s");

        var retry = log.Single(l => l.StartsWith("retrying", StringComparison.Ordinal));

        retry.ShouldContain("Orders.Specs.flaky_broker");
        retry.ShouldContain("System.TimeoutException");
        retry.ShouldNotContain("9f2c41aa");
    }

    [Fact]
    public async Task a_clean_pass_carries_no_recovered_failure()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("steady")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var test = JsonDocument.Parse(RunReport.ToJson(await new Supervisor(factory).Run())).RootElement
            .GetProperty("tests").EnumerateArray().Single();

        test.GetProperty("recoveredFrom").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
