using System.Globalization;
using System.Text;

namespace Bobcat.Supervisor;

/// <summary>
/// A supervised run's results as a GitHub Actions job summary (bobcat#700): one table row per
/// project, then what a reader needs to act on, under each project's name.
/// </summary>
/// <remarks>
/// <para>
/// Every adopter wrote this by hand. Collect the rows and write once, because a Markdown table is
/// only a table while its rows are contiguous: appending a row per project with anything in
/// between breaks it.
/// </para>
/// <code>
/// var summary = new GitHubSummary("Supervised tests");
/// summary.Add("Marten.Testing (net9.0)", results);
/// summary.Add("EventSourcingTests (net9.0)", more);
/// summary.Write();   // appends to $GITHUB_STEP_SUMMARY; does nothing off GitHub Actions
/// </code>
/// <para>
/// <b>Writing can never fail a run.</b> An unwritable summary file is reported through the
/// returned value and nothing else, because reporting about the tests must not be what fails them.
/// </para>
/// </remarks>
public sealed class GitHubSummary
{
    /// <summary>The variable GitHub Actions names the job summary file with.</summary>
    public const string SummaryVariable = "GITHUB_STEP_SUMMARY";

    /// <summary>How many tests each list names before "…and N more".</summary>
    public const int MaxListed = 25;

    private readonly List<(string Project, SupervisorResults Results)> _projects = [];

    /// <param name="heading">The summary's heading; none when null.</param>
    public GitHubSummary(string? heading = null) => Heading = heading;

    /// <summary>The summary's heading, written as a level-3 Markdown heading.</summary>
    public string? Heading { get; }

    /// <summary>The projects added so far, in order.</summary>
    public IReadOnlyList<string> Projects => _projects.Select(x => x.Project).ToList();

    /// <summary>Adds one project's row.</summary>
    /// <param name="project">The row's name: usually the test project and its target framework.</param>
    /// <param name="results">That project's supervised run.</param>
    public GitHubSummary Add(string project, SupervisorResults results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(results);
        _projects.Add((project, results));
        return this;
    }

    /// <summary>One run's summary, written straight away.</summary>
    /// <returns>Where it was written, or null when there is no summary file or it could not be written.</returns>
    public static string? Write(SupervisorResults results, string project, string? heading = null)
        => new GitHubSummary(heading).Add(project, results).Write();

    /// <summary>
    /// Appends the summary to the file <c>$GITHUB_STEP_SUMMARY</c> names, or to
    /// <paramref name="path"/> when one is given.
    /// </summary>
    /// <returns>Where it was written, or null when there is no summary file or it could not be written.</returns>
    public string? Write(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable(SummaryVariable);
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            File.AppendAllText(path, ToMarkdown());
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The summary's Markdown.</summary>
    public string ToMarkdown()
    {
        var markdown = new StringBuilder();

        if (Heading is not null)
        {
            markdown.AppendLine($"### {Heading}").AppendLine();
        }

        if (_projects.Count == 0)
        {
            return markdown.AppendLine("No supervised runs were reported.").AppendLine().ToString();
        }

        markdown.AppendLine("| project | result | tests | passed | passed on retry | failed | indeterminate | skipped | retries | duration |");
        markdown.AppendLine("|---|---|--:|--:|--:|--:|--:|--:|--:|--:|");

        foreach (var (project, results) in _projects)
        {
            markdown.AppendLine(
                $"| {cell(project)} | {verdict(results)} | {results.Tests.Count} | {results.CleanPasses.Count} | "
                + $"{mark(results.PassedOnRetry.Count)} | {mark(results.Failed.Count)} | {mark(results.Indeterminate.Count)} | "
                + $"{results.Skipped.Count} | {mark(results.RetriesPerformed)} | {duration(results.Duration)} |");
        }

        markdown.AppendLine();

        foreach (var (project, results) in _projects)
        {
            details(markdown, project, results);
        }

        return markdown.ToString();
    }

    /// <summary>The row's one-word verdict, worst first.</summary>
    private static string verdict(SupervisorResults results) => results switch
    {
        { IsPartial: true } => "⚠️ partial",
        { AbortReason: not null } => "❌ aborted",
        { ExitCode: 2 } => "❌ indeterminate",
        { ExitCode: 1 } => "❌ failed",
        { Tests.Count: 0 } => "⚠️ no tests",
        { PassedOnRetry.Count: > 0 } => "⚠️ flaky",
        _ => "✅ passed"
    };

    private static void details(StringBuilder markdown, string project, SupervisorResults results)
    {
        var lines = new List<string>();

        if (results.AbortReason is not null)
        {
            lines.Add($"> **Aborted:** {inline(results.AbortReason)}");
            lines.Add("");
        }

        if (results.CollapsedFailure is { } collapsedFailure)
        {
            lines.Add($"> **Failed on collapsed theories:** {inline(collapsedFailure)}");
            lines.Add("");
        }

        list(lines, "Failed", results.Failed, test => $"{code(test.DisplayName)} — {inline(GitHubAnnotations.ReasonFor(test))}");
        list(lines, "Indeterminate (result never established)", results.Indeterminate,
            test => $"{code(test.DisplayName)} — {inline(test.Final.Outcome.ErrorMessage ?? "no result reported")}");
        list(lines, "Passed only on a retry", results.PassedOnRetry,
            test => $"{code(test.DisplayName)} — {test.AttemptCount} attempts"
                    + (RunReport.RecoveredFrom(test) is { } failure ? $", first failed with {inline(failure.ToString())}" : ""));
        list(lines, "Stalled", results.StalledTests,
            stalled => $"{code(stalled.DisplayName)} — {(int)stalled.InFlight.TotalSeconds}s in flight on lane {stalled.Worker.Lane}");
        list(lines, "Worker faults", results.WorkerFaults, fault => inline(fault));

        if (lines.Count == 0) return;

        markdown.AppendLine($"<details><summary>{html(project)}</summary>").AppendLine();
        foreach (var line in lines) markdown.AppendLine(line);
        markdown.AppendLine("</details>").AppendLine();
    }

    private static void list<T>(List<string> lines, string title, IReadOnlyList<T> items, Func<T, string> describe)
    {
        if (items.Count == 0) return;

        lines.Add($"**{title}** ({items.Count})");
        lines.Add("");
        foreach (var item in items.Take(MaxListed)) lines.Add($"- {describe(item)}");
        if (items.Count > MaxListed) lines.Add($"- …and {items.Count - MaxListed} more");
        lines.Add("");
    }

    // Zero reads as zero; anything else is bold, because the eye is meant to stop on it
    private static string mark(int count) => count == 0 ? "0" : $"**{count}**";

    private static string duration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string cell(string text) => inline(text).Replace("|", "\\|");

    private static string code(string text) => $"`{inline(text).Replace("`", "'")}`";

    private static string html(string text) => inline(text).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>The first line, trimmed and capped: a stack trace does not belong in a summary.</summary>
    private static string inline(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}

/// <summary>
/// A supervised run's failures as GitHub Actions workflow commands (bobcat#700): an
/// <c>::error</c> per failed or indeterminate test and for an aborted run, a <c>::warning</c> for
/// retries, stalls and a partial run, so they reach the run's Annotations panel — a green run
/// that only passed after retries included.
/// </summary>
/// <remarks>
/// GitHub shows only the first ten annotations of each kind per step, so at most
/// <see cref="MaxPerKind"/> tests are named and the rest are counted in one more annotation.
/// </remarks>
public static class GitHubAnnotations
{
    /// <summary>The variable GitHub Actions sets to <c>true</c> in every step.</summary>
    public const string ActionsVariable = "GITHUB_ACTIONS";

    /// <summary>How many tests are annotated one by one before the rest are counted.</summary>
    public const int MaxPerKind = 10;

    /// <summary>True inside a GitHub Actions step.</summary>
    public static bool IsGitHubActions
        => string.Equals(Environment.GetEnvironmentVariable(ActionsVariable), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the annotations to standard output (or <paramref name="output"/>), where the runner
    /// reads workflow commands. Does nothing off GitHub Actions unless an output is given.
    /// </summary>
    /// <param name="results">The supervised run.</param>
    /// <param name="project">Names the run in every annotation's title.</param>
    /// <param name="output">Where to write; standard output when null.</param>
    public static void Write(SupervisorResults results, string project, TextWriter? output = null)
    {
        if (output is null && !IsGitHubActions) return;

        output ??= Console.Out;
        foreach (var line in For(results, project))
        {
            output.WriteLine(line);
        }
    }

    /// <summary>The workflow command lines for <paramref name="results"/>, without writing them.</summary>
    public static IReadOnlyList<string> For(SupervisorResults results, string project)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        var lines = new List<string>();

        if (results.AbortReason is not null)
        {
            lines.Add(command("error", $"{project}: run aborted", results.AbortReason));
        }

        if (results.CollapsedFailure is { } collapsedFailure)
        {
            lines.Add(command("error", $"{project}: {results.Collapsed.Count} collapsed theory(ies)", collapsedFailure));
        }

        each(lines, "error", project, "failed", results.Failed, ReasonFor);
        each(lines, "error", project, "indeterminate", results.Indeterminate,
            test => test.Final.Outcome.ErrorMessage ?? "no result reported");

        if (results.PassedOnRetry.Count > 0)
        {
            var names = results.PassedOnRetry.Take(MaxPerKind).Select(t => t.DisplayName);
            var more = results.PassedOnRetry.Count > MaxPerKind ? $"\n…and {results.PassedOnRetry.Count - MaxPerKind} more" : "";
            lines.Add(command("warning", $"{project}: {results.PassedOnRetry.Count} test(s) passed only on a retry",
                string.Join("\n", names) + more));
        }

        if (results.StalledTests.Count > 0)
        {
            var names = results.StalledTests.Take(MaxPerKind)
                .Select(t => $"{t.DisplayName} ({(int)t.InFlight.TotalSeconds}s, lane {t.Worker.Lane})");
            var more = results.StalledTests.Count > MaxPerKind ? $"\n…and {results.StalledTests.Count - MaxPerKind} more" : "";
            lines.Add(command("warning", $"{project}: {results.StalledTests.Count} stalled test(s)", string.Join("\n", names) + more));
        }

        if (results.IsPartial)
        {
            lines.Add(command("warning", $"{project}: partial results",
                "The run was still in flight when these results were taken; tests without a verdict are indeterminate."));
        }

        return lines;
    }

    /// <summary>What a failed test failed with: its error type and the first line of its message.</summary>
    public static string ReasonFor(TestReport test)
    {
        var outcome = test.Final.Outcome;
        var message = outcome.ErrorMessage?.Split('\n', 2)[0].Trim();
        return (outcome.ErrorType, message) switch
        {
            (null, null or "") => test.Final.Disposition.Reason,
            (null, _) => message!,
            (_, null or "") => outcome.ErrorType!,
            _ => $"{outcome.ErrorType}: {message}"
        };
    }

    private static void each(List<string> lines, string kind, string project, string what,
        IReadOnlyList<TestReport> tests, Func<TestReport, string> message)
    {
        foreach (var test in tests.Take(MaxPerKind))
        {
            lines.Add(command(kind, $"{project}: {what}: {test.DisplayName}", message(test)));
        }

        if (tests.Count > MaxPerKind)
        {
            lines.Add(command(kind, $"{project}: {tests.Count - MaxPerKind} more {what}",
                string.Join("\n", tests.Skip(MaxPerKind).Take(50).Select(t => t.DisplayName))
                + (tests.Count > MaxPerKind + 50 ? $"\n…and {tests.Count - MaxPerKind - 50} more" : "")));
        }
    }

    /// <summary>
    /// One workflow command, escaped as the runner expects: a property escapes <c>%</c>, line
    /// breaks, <c>:</c> and <c>,</c>; the message escapes <c>%</c> and line breaks, so a
    /// multi-line message stays one command.
    /// </summary>
    private static string command(string kind, string title, string message)
        => $"::{kind} title={escapeProperty(title)}::{escapeData(message)}";

    private static string escapeData(string text)
        => text.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    private static string escapeProperty(string text)
        => escapeData(text).Replace(":", "%3A").Replace(",", "%2C");
}
