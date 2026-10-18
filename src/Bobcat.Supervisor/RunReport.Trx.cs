using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Bobcat.Resilience;

namespace Bobcat.Supervisor;

/// <summary>What a TRX needs that a <see cref="SupervisorResults"/> does not carry (bobcat#699).</summary>
public sealed record TrxOptions
{
    /// <summary>When the run started. Defaults to now less the run's duration.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>The test assembly's path, written as each test's <c>storage</c> and <c>codeBase</c>.</summary>
    public string? Storage { get; init; }

    /// <summary>The run's name. Defaults to "Supervised run (N worker(s))".</summary>
    public string? RunName { get; init; }

    public string ComputerName { get; init; } = Environment.MachineName;
}

public static partial class RunReport
{
    private static readonly XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    // VSTest's well-known ids: the "unit test" type and the "Results Not in a List" list.
    private const string unitTestType = "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b";
    private const string resultsNotInAList = "8c84fa94-04c1-424b-9868-57a2d4851a1d";
    private const string allLoadedResults = "19431567-8539-422a-85d7-44ee4e166bda";

    /// <summary>
    /// The CI view (bobcat#699): a Visual Studio TRX file for reporters and scoreboards that read
    /// one. A worker in MTP server mode cannot write its own, and none of them sees the whole run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <c>UnitTestResult</c> per test, so a reporter counts what the run counted. A test that
    /// took more than one attempt carries every attempt as an <c>InnerResults</c> entry — outcome,
    /// duration, message and stack trace each — and its own output says what the attempts were.
    /// </para>
    /// <para>
    /// TRX has no word for the honest statuses, so each is said in the text rather than dropped:
    /// a pass on retry is <c>Passed</c> with output opening <c>PASSED ON RETRY</c> and a run-level
    /// warning; an indeterminate test is <c>Failed</c> with a message opening <c>INDETERMINATE</c>,
    /// never a skip a reporter would wave through. A duration nobody measured is written as zero,
    /// because readers require the attribute — <see cref="ToJson"/> is where it stays null.
    /// </para>
    /// </remarks>
    public static string ToTrx(SupervisorResults results, TrxOptions? options = null)
    {
        var document = trxDocument(results, options ?? new TrxOptions());

        var builder = new StringBuilder();
        using (var writer = new Utf8StringWriter(builder)) document.Save(writer);
        return builder.ToString();
    }

    /// <summary>Writes <see cref="ToTrx"/> to <paramref name="path"/>, creating its directory.</summary>
    public static void WriteTrx(SupervisorResults results, string path, TrxOptions? options = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);

        File.WriteAllText(path, ToTrx(results, options), new UTF8Encoding(false));
    }

    private static XDocument trxDocument(SupervisorResults results, TrxOptions options)
    {
        var started = options.StartedAt ?? DateTimeOffset.Now - results.Duration;
        var finished = started + results.Duration;
        var storage = options.Storage ?? "";

        var definitions = new XElement(trx + "TestDefinitions");
        var unitResults = new XElement(trx + "Results");
        var entries = new XElement(trx + "TestEntries");

        int passed = 0, failed = 0, notExecuted = 0;

        foreach (var test in results.Tests)
        {
            var testId = stableId(test.Uid);
            var executionId = stableId("execution:" + test.Uid);
            var final = test.Final.Outcome;
            var outcome = trxOutcome(test.Outcome);

            switch (outcome)
            {
                case "Passed": passed++; break;
                case "NotExecuted": notExecuted++; break;
                default: failed++; break;
            }

            definitions.Add(new XElement(trx + "UnitTest",
                new XAttribute("name", test.DisplayName),
                new XAttribute("storage", storage),
                new XAttribute("id", testId),
                new XElement(trx + "Execution", new XAttribute("id", executionId)),
                new XElement(trx + "TestMethod",
                    new XAttribute("codeBase", storage),
                    new XAttribute("adapterTypeName", "executor://bobcat/supervisor"),
                    // The partitioner's reading of a display name, so the class a reporter groups by
                    // is the class the run kept together.
                    new XAttribute("className", final.TestClass
                        ?? WorkPlan.ClassOf(new WorkerTest(test.Uid, test.DisplayName))),
                    new XAttribute("name", test.DisplayName))));

            entries.Add(new XElement(trx + "TestEntry",
                new XAttribute("testId", testId),
                new XAttribute("executionId", executionId),
                new XAttribute("testListId", resultsNotInAList)));

            var measured = test.Attempts.Select(a => a.Outcome.Duration).Where(d => d.HasValue).ToList();
            var duration = measured.Aggregate(TimeSpan.Zero, (sum, d) => sum + d!.Value);

            var result = unitTestResult(executionId, testId, test.DisplayName, outcome, duration,
                started, finished, options.ComputerName);

            result.Add(output(stdOut(test), errorInfo(test.Outcome, final, outcome)));

            if (test.AttemptCount > 1)
            {
                var inner = new XElement(trx + "InnerResults");
                foreach (var attempt in test.Attempts)
                {
                    var attemptOutcome = trxOutcome(attempt.Outcome.State);
                    var attemptResult = unitTestResult(
                        stableId($"execution:{test.Uid}:{attempt.AttemptNumber}"), testId,
                        $"{test.DisplayName} (attempt {attempt.AttemptNumber})", attemptOutcome,
                        attempt.Outcome.Duration ?? TimeSpan.Zero, started, finished, options.ComputerName);

                    attemptResult.Add(new XAttribute("parentExecutionId", executionId));
                    attemptResult.Add(output(describeAttempt(attempt),
                        errorInfo(null, attempt.Outcome, attemptOutcome)));

                    inner.Add(attemptResult);
                }

                result.Add(inner);
            }

            unitResults.Add(result);
        }

        var total = results.Tests.Count;

        var run = new XElement(trx + "TestRun",
            new XAttribute("id", Guid.NewGuid()),
            new XAttribute("name", options.RunName ?? $"Supervised run ({results.WorkersLaunched} worker(s))"),
            new XElement(trx + "Times",
                new XAttribute("creation", timestamp(started)),
                new XAttribute("queuing", timestamp(started)),
                new XAttribute("start", timestamp(started)),
                new XAttribute("finish", timestamp(finished))),
            definitions,
            entries,
            new XElement(trx + "TestLists",
                new XElement(trx + "TestList",
                    new XAttribute("name", "Results Not in a List"),
                    new XAttribute("id", resultsNotInAList)),
                new XElement(trx + "TestList",
                    new XAttribute("name", "All Loaded Results"),
                    new XAttribute("id", allLoadedResults))),
            unitResults,
            new XElement(trx + "ResultSummary",
                new XAttribute("outcome", summaryOutcome(results)),
                new XElement(trx + "Counters",
                    new XAttribute("total", total),
                    new XAttribute("executed", total - notExecuted),
                    new XAttribute("passed", passed),
                    new XAttribute("failed", failed),
                    new XAttribute("error", 0),
                    new XAttribute("timeout", 0),
                    new XAttribute("aborted", 0),
                    new XAttribute("inconclusive", 0),
                    new XAttribute("passedButRunAborted", 0),
                    new XAttribute("notRunnable", 0),
                    new XAttribute("notExecuted", notExecuted),
                    new XAttribute("disconnected", 0),
                    new XAttribute("warning", 0),
                    new XAttribute("completed", 0),
                    new XAttribute("inProgress", 0),
                    new XAttribute("pending", 0)),
                new XElement(trx + "Output", new XElement(trx + "StdOut", results.Summarize())),
                runInfos(results, finished, options.ComputerName)));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), run);
    }

    private static XElement unitTestResult(
        Guid executionId, Guid testId, string name, string outcome, TimeSpan duration,
        DateTimeOffset started, DateTimeOffset finished, string computer)
        => new(trx + "UnitTestResult",
            new XAttribute("executionId", executionId),
            new XAttribute("testId", testId),
            new XAttribute("testName", name),
            new XAttribute("computerName", computer),
            new XAttribute("duration", duration.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture)),
            new XAttribute("startTime", timestamp(started)),
            new XAttribute("endTime", timestamp(finished)),
            new XAttribute("testType", unitTestType),
            new XAttribute("outcome", outcome),
            new XAttribute("testListId", resultsNotInAList),
            new XAttribute("relativeResultsDirectory", ""));

    // A pass on retry is a pass, as it is everywhere else; it is the output that says it was not
    // a clean one. Aborted and indeterminate are failures, never skips.
    private static string trxOutcome(RunOutcome outcome) => outcome switch
    {
        RunOutcome.CleanPass or RunOutcome.PassOnRetry => "Passed",
        RunOutcome.Skipped => "NotExecuted",
        _ => "Failed"
    };

    private static string trxOutcome(WorkerTestState state) => state switch
    {
        WorkerTestState.Passed => "Passed",
        WorkerTestState.Skipped => "NotExecuted",
        _ => "Failed"
    };

    private static string summaryOutcome(SupervisorResults results)
    {
        if (results.AbortReason is not null || results.IsPartial) return "Aborted";
        return results.ExitCode == 0 ? "Completed" : "Failed";
    }

    private static XElement? output(string? stdOut, XElement? error)
    {
        if (stdOut is null && error is null) return null;

        var output = new XElement(trx + "Output");
        if (stdOut is not null) output.Add(new XElement(trx + "StdOut", stdOut));
        if (error is not null) output.Add(error);
        return output;
    }

    private static XElement? errorInfo(RunOutcome? testOutcome, WorkerOutcome outcome, string trxOutcome)
    {
        if (trxOutcome == "Passed") return null;

        string? message;
        if (outcome.State == WorkerTestState.Indeterminate)
        {
            message = "INDETERMINATE: the run never established a result for this test, almost always "
                      + "because its worker died. The run's worker faults name the exit code and stderr."
                      + (outcome.ErrorMessage is { Length: > 0 } detail ? Environment.NewLine + detail : "");
        }
        else
        {
            message = withType(outcome.ErrorType, outcome.ErrorMessage);
            if (testOutcome == RunOutcome.Aborted)
            {
                message = "RUN ABORTED at this test." + (message is null ? "" : Environment.NewLine + message);
            }
        }

        // A skip with nothing to say needs no error block.
        if (trxOutcome == "NotExecuted" && message is null) return null;

        var info = new XElement(trx + "ErrorInfo",
            new XElement(trx + "Message", message ?? $"Test {outcome.State.ToString().ToLowerInvariant()}."));

        if (!string.IsNullOrWhiteSpace(outcome.StackTrace))
        {
            info.Add(new XElement(trx + "StackTrace", outcome.StackTrace));
        }

        return info;
    }

    private static string? stdOut(TestReport test)
    {
        var lines = new List<string>();

        if (test.Outcome == RunOutcome.PassOnRetry)
        {
            lines.Add($"PASSED ON RETRY: passed on attempt {test.AttemptCount} of {test.AttemptCount}; not a clean pass."
                      + (RecoveredFrom(test) is { } failure ? $" First failed with {failure}." : ""));
        }

        if (test.AttemptCount > 1)
        {
            lines.AddRange(test.Attempts.Select(describeAttempt));
        }

        if (test.IsCollapsed)
        {
            lines.Add($"COLLAPSED: {collapsed(test, everyRow: false)}");
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static string describeAttempt(SupervisorAttempt attempt)
    {
        var line = $"Attempt {attempt.AttemptNumber} ({attempt.Placement}): {attempt.Outcome.State}"
                   + $" — {attempt.Disposition.Kind}: {attempt.Disposition.Reason}";

        if (attempt.StallInduced) line += " [stall-induced]";
        if (attempt.Unsupported is not null) line += $" [not honoured: {attempt.Unsupported}]";
        return line;
    }

    private static XElement? runInfos(SupervisorResults results, DateTimeOffset at, string computer)
    {
        var infos = new List<(string Outcome, string Text)>();

        if (results.AbortReason is not null) infos.Add(("Error", $"RUN ABORTED: {results.AbortReason}"));
        if (results.IsPartial) infos.Add(("Warning", "PARTIAL: the run was still in flight when this report was taken."));
        infos.AddRange(results.WorkerFaults.Select(fault => ("Error", $"Worker fault: {fault}")));

        if (results.PassedOnRetry.Count > 0)
        {
            infos.Add(("Warning", $"{results.PassedOnRetry.Count} test(s) passed on retry, not cleanly: "
                                  + string.Join(", ", results.PassedOnRetry.Select(t => t.DisplayName))));
        }

        infos.AddRange(results.StallKills.Select(kill =>
            ("Warning", $"Worker killed to clear a stall in {kill.DisplayName} after {kill.InFlight}.")));

        infos.AddRange(results.UnsupportedDispositions.Select(u => ("Warning", $"Retry request not honoured: {u}")));

        if (infos.Count == 0) return null;

        return new XElement(trx + "RunInfos", infos.Select(info => new XElement(trx + "RunInfo",
            new XAttribute("computerName", computer),
            new XAttribute("outcome", info.Outcome),
            new XAttribute("timestamp", timestamp(at)),
            new XElement(trx + "Text", info.Text))));
    }

    private static string? withType(string? type, string? message)
    {
        if (string.IsNullOrWhiteSpace(type)) return string.IsNullOrWhiteSpace(message) ? null : message;
        if (message is null) return type;

        // xUnit already writes "Ns.Type : message"; don't say the type twice.
        return message.StartsWith(type, StringComparison.Ordinal) ? message : $"{type}: {message}";
    }

    // Stable per test, so two runs' files differ by outcome rather than by identity.
    private static Guid stableId(string value) => new(MD5.HashData(Encoding.UTF8.GetBytes(value)));

    private static string timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
