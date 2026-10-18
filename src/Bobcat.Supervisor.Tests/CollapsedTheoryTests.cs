using System.Text.Json;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#649: a theory whose data the framework cannot serialize is discovered as ONE test, and
/// every row reports its verdict under that one uid. Keeping only the last verdict let Fisher's
/// supervised run go green over a failure a direct run reported.
/// </summary>
public class CollapsedTheoryTests
{
    private static WorkerOutcome row(string name, WorkerTestState state, double? ms = null)
        => new("theory-uid", name, state)
        {
            ErrorMessage = state == WorkerTestState.Passed ? null : $"{name} failed",
            Duration = ms is null ? null : TimeSpan.FromMilliseconds(ms.Value)
        };

    [Fact]
    public void a_later_rows_pass_never_hides_an_earlier_rows_failure()
    {
        var folded = MtpWorkerClient.FoldTerminal(
            row("reused(value: 1)", WorkerTestState.Failed),
            row("reused(value: 2)", WorkerTestState.Passed));

        folded.State.ShouldBe(WorkerTestState.Failed);
        folded.ErrorMessage.ShouldBe("reused(value: 1) failed");
        folded.IsCollapsed.ShouldBeTrue();
        folded.CollapsedDisplayNames.ShouldBe(["reused(value: 1)", "reused(value: 2)"]);
    }

    [Fact]
    public void the_first_failure_is_the_one_reported_and_every_rows_time_counts()
    {
        var folded = new[]
        {
            row("a", WorkerTestState.Passed, 10),
            row("b", WorkerTestState.Failed, 20),
            row("c", WorkerTestState.Error, 30),
            row("d", WorkerTestState.Passed, 40)
        }.Aggregate(MtpWorkerClient.FoldTerminal);

        folded.State.ShouldBe(WorkerTestState.Failed);
        folded.DisplayName.ShouldBe("b");
        folded.Duration.ShouldBe(TimeSpan.FromMilliseconds(100));
        folded.CollapsedDisplayNames.ShouldBe(["a", "b", "c", "d"]);
    }

    [Fact]
    public void a_repeated_final_update_for_the_same_row_is_not_a_collapse()
    {
        var folded = MtpWorkerClient.FoldTerminal(
            row("only", WorkerTestState.Passed),
            row("only", WorkerTestState.Passed));

        folded.State.ShouldBe(WorkerTestState.Passed);
        folded.IsCollapsed.ShouldBeFalse();
    }

    [Fact]
    public void a_pass_outranks_a_skip()
    {
        MtpWorkerClient.FoldTerminal(row("a", WorkerTestState.Skipped), row("b", WorkerTestState.Passed))
            .State.ShouldBe(WorkerTestState.Passed);
    }

    private static Task<SupervisorResults> collapsedRun(WorkerTestState state, bool failOnCollapsed = false) => new Supervisor(
        new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("reused_command_binding"), FakeWorkerFactory.Test("plain")],
            Outcome = (uid, _, _) => uid == "plain" ? WorkerTestState.Passed : state,
            CollapsedRows = uid => uid == "plain"
                ? null
                : ["reused_command_binding(value: 1)", "reused_command_binding(value: 2)"]
        }) { FailOnCollapsed = failOnCollapsed }.Run();

    [Fact]
    public async Task the_report_names_a_collapsed_theory_even_when_it_passed()
    {
        // The fix belongs in the suite (serializable data), and the run saw fewer tests than a
        // direct run would — so a green collapse is still worth a line.
        var results = await collapsedRun(WorkerTestState.Passed);

        results.Collapsed.ShouldHaveSingleItem().Uid.ShouldBe("reused_command_binding");
        results.Summarize().ShouldContain("1 collapsed theory(ies)");

        var text = RunReport.ToText(results);
        text.ShouldContain("Collapsed theories");
        text.ShouldContain("2 rows, judged CleanPass by the worst");
        text.ShouldContain("reused_command_binding(value: 2)");
    }

    [Fact]
    public async Task the_json_lists_the_collapse_and_its_rows()
    {
        var results = await collapsedRun(WorkerTestState.Failed);
        results.ExitCode.ShouldBe(1);

        var json = JsonDocument.Parse(RunReport.ToJson(results)).RootElement;
        json.GetProperty("collapsed").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["reused_command_binding"]);

        var test = json.GetProperty("tests").EnumerateArray()
            .Single(t => t.GetProperty("uid").GetString() == "reused_command_binding");
        test.GetProperty("outcome").GetString().ShouldBe(nameof(RunOutcome.Failed));
        test.GetProperty("attemptDetail")[0].GetProperty("collapsedRows").GetArrayLength().ShouldBe(2);

        json.GetProperty("tests").EnumerateArray()
            .Single(t => t.GetProperty("uid").GetString() == "plain")
            .GetProperty("attemptDetail")[0].GetProperty("collapsedRows").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task a_green_collapse_passes_by_default()
    {
        var results = await collapsedRun(WorkerTestState.Passed);

        results.ExitCode.ShouldBe(0);
        results.CollapsedFailure.ShouldBeNull();
        RunReport.ToText(results).ShouldNotContain("RUN FAILED");
    }

    [Fact]
    public async Task fail_on_collapsed_fails_a_run_whose_every_row_passed()
    {
        // bobcat#752: otherwise a suite can carry collapsed theories forever.
        var results = await collapsedRun(WorkerTestState.Passed, failOnCollapsed: true);

        results.Failed.ShouldBeEmpty();
        results.ExitCode.ShouldBe(1);
        results.CollapsedFailure.ShouldNotBeNull();
        results.CollapsedFailure.ShouldContain("reused_command_binding");
        results.CollapsedFailure.ShouldContain("pass a row key and look the value up inside the test");

        var text = RunReport.ToText(results);
        text.ShouldContain("RUN FAILED: 1 collapsed theory(ies)");
        text.ShouldContain("reused_command_binding(value: 1); reused_command_binding(value: 2)");

        var json = JsonDocument.Parse(RunReport.ToJson(results)).RootElement;
        json.GetProperty("exitCode").GetInt32().ShouldBe(1);
        json.GetProperty("failOnCollapsed").GetBoolean().ShouldBeTrue();
        json.GetProperty("collapsedFailure").GetString().ShouldBe(results.CollapsedFailure);

        GitHubAnnotations.For(results, "Specs").ShouldContain(line => line.StartsWith("::error") && line.Contains("collapsed theory"));
    }

    [Fact]
    public async Task fail_on_collapsed_names_every_row_not_the_first_five()
    {
        var rows = Enumerable.Range(1, 7).Select(i => $"wide(value: {i})").ToList();
        var results = await new Supervisor(new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("wide")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            CollapsedRows = _ => rows
        }) { FailOnCollapsed = true }.Run();

        var text = RunReport.ToText(results);
        text.ShouldContain("wide(value: 7)");
        text.ShouldNotContain("…and");
    }

    [Fact]
    public async Task fail_on_collapsed_does_nothing_without_a_collapse()
    {
        var results = await new Supervisor(new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("plain")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        }) { FailOnCollapsed = true }.Run();

        results.ExitCode.ShouldBe(0);
        results.CollapsedFailure.ShouldBeNull();
    }
}
