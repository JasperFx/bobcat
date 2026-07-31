using JasperFx.Testing;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Recovery hints across the process boundary. The supervisor never loads the worker's assembly,
/// so it matches on the type name the worker reported — and abstains when there is none.
/// </summary>
public class SupervisorRecoveryHintTests
{
    private static RecoveryHint clearsOnRetry(string typeName) => new()
    {
        FailureTypeName = typeName,
        Kind = DispositionKind.RetryInProcess,
        Because = "the broker is slow to warm up",
        Source = "run configuration"
    };

    private static Supervisor supervised(
        FakeWorkerFactory factory, params RecoveryHint[] hints)
    {
        var supervisor = new Supervisor(factory)
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 }
        };

        supervisor.RecoveryHints.AddRange(hints);
        return supervisor;
    }

    [Fact]
    public async Task a_hint_retries_a_failure_the_worker_named_although_nothing_is_tagged()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky")],
            ErrorType = (_, _) => "System.TimeoutException",
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Error
        };

        var results = await supervised(factory, clearsOnRetry("System.TimeoutException")).Run();

        var test = results.Tests.Single();
        test.AttemptCount.ShouldBe(2);
        test.Outcome.ShouldBe(RunOutcome.PassOnRetry);
        test.Attempts[0].Disposition.Reason.ShouldContain("the broker is slow to warm up");
    }

    [Fact]
    public async Task a_hint_matches_the_simple_name_a_worker_reports()
    {
        // Frameworks are inconsistent about qualifying the type. Refusing to match would make
        // hints unusable out of process, which is where the supervisor always is.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky")],
            ErrorType = (_, _) => "TimeoutException",
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Error
        };

        var results = await supervised(factory, clearsOnRetry("System.TimeoutException")).Run();

        results.Tests.Single().AttemptCount.ShouldBe(2);
    }

    [Fact]
    public async Task a_different_failure_type_is_not_retried()
    {
        // Mutation guard: the match must be on the reported type, not on a hint merely existing.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("broken")],
            ErrorType = (_, _) => "System.InvalidOperationException",
            Outcome = (_, _, _) => WorkerTestState.Error
        };

        var results = await supervised(factory, clearsOnRetry("System.TimeoutException")).Run();

        results.Tests.Single().AttemptCount.ShouldBe(1);
        results.ExitCode.ShouldBe(1);
    }

    [Fact]
    public async Task a_worker_that_erases_the_exception_type_gets_no_hint_and_no_retry()
    {
        // tUnit reports no error type at all. Silence is not a match — the run degrades to the
        // tag-driven default rather than guessing which hint the author meant.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("nameless")],
            ErrorType = (_, _) => null,
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Error
        };

        var results = await supervised(factory, clearsOnRetry("System.TimeoutException")).Run();

        results.Tests.Single().AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task a_hint_can_ask_for_a_recycle_the_supervisor_then_performs()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("brokered")],
            ErrorType = (_, _) => "BrokerUnavailableException",
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Error
        };

        var rabbit = new StubRecyclable("rabbit");

        var supervisor = supervised(factory, new RecoveryHint
        {
            FailureTypeName = "BrokerUnavailableException",
            Kind = DispositionKind.RetryAfterRecycle,
            Resources = ["rabbit"],
            Because = "the broker wedges under contention",
            Source = "run configuration"
        });

        supervisor.AddRecyclableResource(rabbit);

        var results = await supervisor.Run();

        rabbit.Recycled.ShouldBe(1);
        results.Recyclings.ShouldBe(["rabbit"]);
        results.Tests.Single().Outcome.ShouldBe(RunOutcome.PassOnRetry);
    }

    [Fact]
    public async Task a_never_recovers_hint_stops_a_tagged_test_retrying()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("tagged", "Retry=2")],
            ErrorType = (_, _) => "System.NotSupportedException",
            Outcome = (_, _, _) => WorkerTestState.Failed
        };

        var results = await supervised(factory, new RecoveryHint
        {
            FailureTypeName = "System.NotSupportedException",
            Kind = DispositionKind.FailAndContinue,
            Because = "this is a real bug",
            Source = "run configuration"
        }).Run();

        var test = results.Tests.Single();
        test.AttemptCount.ShouldBe(1);
        test.Attempts[0].Disposition.Reason.ShouldContain("this is a real bug");
    }

    [Fact]
    public async Task a_run_with_no_hints_is_unchanged()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky")],
            ErrorType = (_, _) => "System.TimeoutException",
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Error
        };

        var results = await supervised(factory).Run();

        results.Tests.Single().AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task the_report_says_which_hint_fired_and_why()
    {
        // "Rely on console output": a hint that suppressed a retry has to be visible, or a
        // tagged test that failed once looks like the tag stopped working.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("tagged", "Retry=2")],
            ErrorType = (_, _) => "System.NotSupportedException",
            Outcome = (_, _, _) => WorkerTestState.Failed
        };

        var results = await supervised(factory, new RecoveryHint
        {
            FailureTypeName = "System.NotSupportedException",
            Kind = DispositionKind.FailAndContinue,
            Because = "this is a real bug",
            Source = "run configuration"
        }).Run();

        var text = RunReport.ToText(results);
        text.ShouldContain("Recovery hints applied");
        text.ShouldContain("this is a real bug");
        text.ShouldContain("tagged");
    }

    [Fact]
    public async Task the_json_carries_the_hint_as_fields_rather_than_prose()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky")],
            ErrorType = (_, _) => "System.TimeoutException",
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Error
        };

        var results = await supervised(factory, clearsOnRetry("System.TimeoutException")).Run();

        var hint = System.Text.Json.JsonDocument.Parse(RunReport.ToJson(results)).RootElement
            .GetProperty("tests").EnumerateArray().Single()
            .GetProperty("attemptDetail").EnumerateArray().First()
            .GetProperty("hint");

        hint.GetProperty("failureType").GetString().ShouldBe("System.TimeoutException");
        hint.GetProperty("recovery").GetString().ShouldBe(nameof(DispositionKind.RetryInProcess));
        hint.GetProperty("because").GetString().ShouldBe("the broker is slow to warm up");
        hint.GetProperty("declaredOn").GetString().ShouldBe("run configuration");
    }

    [Fact]
    public async Task a_run_with_no_hints_reports_no_hint_section_and_a_null_hint_field()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("broken")],
            Outcome = (_, _, _) => WorkerTestState.Failed
        };

        var results = await supervised(factory).Run();

        RunReport.ToText(results).ShouldNotContain("Recovery hints");

        System.Text.Json.JsonDocument.Parse(RunReport.ToJson(results)).RootElement
            .GetProperty("tests").EnumerateArray().Single()
            .GetProperty("attemptDetail").EnumerateArray().First()
            .GetProperty("hint").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    private sealed class StubRecyclable(string name) : Bobcat.Runtime.IRecyclableResource
    {
        public string Name { get; } = name;
        public int Recycled { get; private set; }

        public Task Recycle(CancellationToken token = default)
        {
            Recycled++;
            return Task.CompletedTask;
        }

        public Task Start() => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }
}
