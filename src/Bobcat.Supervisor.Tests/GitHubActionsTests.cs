using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>The job summary and annotations every adopter used to write by hand (bobcat#700).</summary>
public class GitHubActionsTests
{
    private static async Task<SupervisorResults> run(
        IReadOnlyList<WorkerTest> tests,
        Func<string, int, FakeWorker, WorkerTestState?> outcome,
        RetryBudget? budget = null,
        Func<string, int, string?>? errorType = null)
    {
        var factory = new FakeWorkerFactory
        {
            Tests = tests,
            Outcome = outcome,
            ErrorType = errorType ?? ((_, _) => null),
            ErrorMessageFor = (uid, attempt) => $"{uid} went wrong on attempt {attempt}\n   at Some.Stack.Frame()"
        };

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
            _ => WorkerTestState.Failed
        },
        new RetryBudget { MaxAttemptsPerTest = 2 },
        (uid, _) => uid == "broken" ? "Shouldly.ShouldAssertException" : null);

    private static Task<SupervisorResults> greenRun() => run(
        [FakeWorkerFactory.Test("a"), FakeWorkerFactory.Test("b")],
        (_, _, _) => WorkerTestState.Passed);

    // ---------------------------------------------------------------- the summary

    [Fact]
    public async Task every_project_is_one_row_of_one_table()
    {
        var markdown = new GitHubSummary("Supervised tests")
            .Add("Core (net9.0)", await mixedRun())
            .Add("Other (net10.0)", await greenRun())
            .ToMarkdown();

        var lines = markdown.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        lines[0].ShouldBe("### Supervised tests");

        var header = lines.FindIndex(l => l.StartsWith("| project |"));
        header.ShouldBeGreaterThan(0);
        lines[header + 1].ShouldStartWith("|---|");

        // Contiguous rows, or it is not a table
        lines[header + 2].ShouldStartWith("| Core (net9.0) | ❌ failed | 3 | 1 | **1** | **1** | 0 | 0 | **1** |");
        lines[header + 3].ShouldStartWith("| Other (net10.0) | ✅ passed | 2 | 2 | 0 | 0 | 0 | 0 | 0 |");
        lines[header + 4].ShouldBe("");
    }

    [Fact]
    public async Task what_needs_acting_on_is_listed_under_the_project_with_the_first_line_of_its_error()
    {
        var markdown = new GitHubSummary().Add("Core", await mixedRun()).ToMarkdown();

        markdown.ShouldContain("<details><summary>Core</summary>");
        markdown.ShouldContain("**Failed** (1)");
        markdown.ShouldContain("- `broken` — Shouldly.ShouldAssertException: broken went wrong on attempt 1");
        markdown.ShouldContain("**Passed only on a retry** (1)");
        markdown.ShouldContain("- `flaky` — 2 attempts, first failed with");

        // A stack trace does not belong in a summary
        markdown.ShouldNotContain("Some.Stack.Frame");
    }

    [Fact]
    public async Task a_clean_project_gets_a_row_and_no_details()
    {
        var markdown = new GitHubSummary().Add("Other", await greenRun()).ToMarkdown();

        markdown.ShouldNotContain("<details>");
    }

    [Fact]
    public void an_aborted_run_says_why_and_an_empty_one_is_not_called_a_pass()
    {
        var aborted = new SupervisorResults { Tests = [], AbortReason = "the database is gone" };
        var empty = new SupervisorResults { Tests = [] };

        var markdown = new GitHubSummary().Add("Aborted", aborted).Add("Empty", empty).ToMarkdown();

        markdown.ShouldContain("| Aborted | ❌ aborted |");
        markdown.ShouldContain("> **Aborted:** the database is gone");
        markdown.ShouldContain("| Empty | ⚠️ no tests |");
    }

    [Fact]
    public void a_pipe_in_a_project_name_does_not_split_its_row()
    {
        var markdown = new GitHubSummary().Add("a|b", new SupervisorResults { Tests = [] }).ToMarkdown();

        markdown.ShouldContain("| a\\|b |");
    }

    [Fact]
    public async Task a_long_list_is_capped_and_says_how_many_more()
    {
        var tests = Enumerable.Range(0, GitHubSummary.MaxListed + 3).Select(i => FakeWorkerFactory.Test($"t{i:00}")).ToList();
        var results = await run(tests, (_, _, _) => WorkerTestState.Failed);

        var markdown = new GitHubSummary().Add("Core", results).ToMarkdown();

        markdown.ShouldContain($"**Failed** ({GitHubSummary.MaxListed + 3})");
        markdown.ShouldContain("- …and 3 more");
    }

    [Fact]
    public async Task write_appends_to_the_summary_file_and_never_replaces_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bobcat-summary-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(path, "earlier step\n", TestContext.Current.CancellationToken);

            new GitHubSummary("Run").Add("Other", await greenRun()).Write(path).ShouldBe(path);

            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            text.ShouldStartWith("earlier step\n### Run");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task an_unwritable_summary_is_reported_by_the_return_value_and_never_thrown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bobcat-missing-{Guid.NewGuid():N}", "summary.md");

        new GitHubSummary().Add("Other", await greenRun()).Write(path).ShouldBeNull();
    }

    // ---------------------------------------------------------------- annotations

    [Fact]
    public async Task a_failed_test_is_an_error_naming_the_project_and_the_test()
    {
        var lines = GitHubAnnotations.For(await mixedRun(), "Core (net9.0)");

        // ':' and ',' are escaped in a property, line breaks in the message
        lines.ShouldContain(
            "::error title=Core (net9.0)%3A failed%3A broken::Shouldly.ShouldAssertException: broken went wrong on attempt 1");
    }

    [Fact]
    public async Task a_pass_on_retry_is_a_warning_so_a_green_run_still_shows_it()
    {
        var lines = GitHubAnnotations.For(await mixedRun(), "Core");

        lines.ShouldContain("::warning title=Core%3A 1 test(s) passed only on a retry::flaky");
    }

    [Fact]
    public async Task a_clean_run_annotates_nothing()
    {
        GitHubAnnotations.For(await greenRun(), "Core").ShouldBeEmpty();
    }

    [Fact]
    public void an_aborted_run_is_an_error_whose_message_stays_one_command()
    {
        var results = new SupervisorResults { Tests = [], AbortReason = "first line\nsecond, with 100%" };

        GitHubAnnotations.For(results, "Core")
            .ShouldBe(["::error title=Core%3A run aborted::first line%0Asecond, with 100%25"]);
    }

    [Fact]
    public async Task beyond_ten_failures_the_rest_are_counted_in_one_more_annotation()
    {
        var tests = Enumerable.Range(0, 13).Select(i => FakeWorkerFactory.Test($"t{i:00}")).ToList();
        var results = await run(tests, (_, _, _) => WorkerTestState.Failed);

        var lines = GitHubAnnotations.For(results, "Core");

        lines.Count.ShouldBe(GitHubAnnotations.MaxPerKind + 1);
        lines[^1].ShouldStartWith("::error title=Core%3A 3 more failed::t10%0At11%0At12");
    }

    [Fact]
    public async Task write_goes_to_the_given_output()
    {
        var output = new StringWriter();

        GitHubAnnotations.Write(await mixedRun(), "Core", output);

        output.ToString().ShouldContain("::error title=Core%3A failed%3A broken::");
    }
}
