using System.Globalization;
using Bobcat.Rendering;
using Bobcat.Resilience;
using Bobcat.Runtime;
using Bobcat.Supervisor;
using Spectre.Console;

namespace Bobcat.Console.Specs;

/// <summary>
/// What makes a <c>bobcat run</c> supervised (bobcat#546): any of <c>--retries</c>,
/// <c>--workers</c>, <c>--stall</c> or <c>--stall-action</c>. Null when none was given, and then the
/// run is the plain one, unchanged.
/// </summary>
internal sealed record Supervision(int Retries, int Workers, TimeSpan Stall, StallAction StallAction)
{
    /// <summary>
    /// How long a test may be in flight before a supervised run names it, when <c>--stall</c> does not
    /// say. Reported, never acted on unless <c>--stall-action</c> asks: hard timeouts belong to the test
    /// framework, and a default kill would turn a slow integration suite into a flaky one.
    /// </summary>
    public static readonly TimeSpan DefaultStall = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The supervision the flags ask for, null for none, or a refusal naming the flag that is wrong.
    /// </summary>
    public static Supervision? From(int? retries, int? workers, string? stall, string? stallAction, out string? refusal)
    {
        refusal = null;
        if (retries is null && workers is null && stall is null && stallAction is null) return null;

        if (retries is < 0)
        {
            refusal = "--retries must be 0 or more.";
            return null;
        }

        if (workers is < 1)
        {
            refusal = "--workers must be 1 or more.";
            return null;
        }

        var threshold = DefaultStall;
        if (stall is not null && !TryParseDuration(stall, out threshold))
        {
            refusal = $"--stall '{stall}' is not a duration; write it as 90s, 5m, 1h, 500ms or 00:05:00.";
            return null;
        }

        var action = StallAction.Report;
        if (stallAction is not null && !TryParseStallAction(stallAction, out action))
        {
            refusal = $"--stall-action '{stallAction}' is not one of report, kill-and-retry, abort.";
            return null;
        }

        return new Supervision(retries ?? 0, workers ?? 1, threshold, action);
    }

    /// <summary>
    /// <c>500ms</c>, <c>90s</c>, <c>5m</c>, <c>1h</c>, a bare number of seconds, or a
    /// <see cref="TimeSpan"/> such as <c>00:05:00</c>. Must be more than zero.
    /// </summary>
    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = default;
        text = text.Trim();

        (string Suffix, Func<double, TimeSpan> Make)[] units =
        [
            ("ms", TimeSpan.FromMilliseconds),
            ("s", TimeSpan.FromSeconds),
            ("m", TimeSpan.FromMinutes),
            ("h", TimeSpan.FromHours)
        ];

        foreach (var (suffix, make) in units)
        {
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            if (double.TryParse(text[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                duration = make(amount);
                return duration > TimeSpan.Zero;
            }

            return false;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            duration = TimeSpan.FromSeconds(seconds);
            return duration > TimeSpan.Zero;
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out duration) && duration > TimeSpan.Zero;
    }

    public static bool TryParseStallAction(string text, out StallAction action)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "report":
                action = StallAction.Report;
                return true;
            case "kill-and-retry" or "killandretry" or "kill":
                action = StallAction.KillAndRetry;
                return true;
            case "abort" or "abortrun" or "abort-run":
                action = StallAction.AbortRun;
                return true;
            default:
                action = StallAction.Report;
                return false;
        }
    }

    /// <summary>The spelling <c>--stall-action</c> takes, so <c>--repeat</c> can record it.</summary>
    public static string Spell(StallAction action) => action switch
    {
        StallAction.KillAndRetry => "kill-and-retry",
        StallAction.AbortRun => "abort",
        _ => "report"
    };

    /// <summary>
    /// <c>--retries N</c> is the per-test ceiling, N retries after the first attempt. A test still needs
    /// its own <c>Retry</c> trait or a recovery hint to be retried at all: retries stay opt-in.
    /// </summary>
    public RetryBudget Budget => Retries == 0 ? RetryBudget.None : new RetryBudget { MaxAttemptsPerTest = Retries + 1 };
}

/// <summary>
/// A <c>bobcat run</c> under the <see cref="Supervisor.Supervisor"/> (bobcat#546): the suite's own test
/// host driven as worker processes, so retries, parallel lanes and stall handling work from the
/// command line rather than only from a hand-written supervisor host.
/// </summary>
/// <remarks>
/// Each worker renders its own specifications into a file of its own, because lanes exit at the
/// same moment and appending to one file from several processes interleaves them. The files are
/// replayed in launch order, then the supervisor's report: what passed on retry, what stalled,
/// what never reported. A test retried in a fresh process is therefore rendered once per attempt,
/// and the report says which attempt counted.
/// </remarks>
internal static class SupervisedHost
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "bobcat-tool");

    public static async Task<RunOutcome> RunAsync(SpecProject project, IReadOnlyList<string> identities,
        RunOptions options, Supervision supervision, CancellationToken token = default)
    {
        Directory.CreateDirectory(Workspace);
        var log = Path.Combine(Workspace, $"{project.Name}.supervised.log");
        var renders = Path.Combine(Workspace, $"{project.Name}.supervised", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(renders);

        // As in SpecHost: every run writes results, because --failed reads them.
        var ownResults = options.ResultsDirectory is null;
        var results = options.ResultsDirectory
                      ?? Path.Combine(Workspace, "results", project.Name, Guid.NewGuid().ToString("n"));

        var environment = new Dictionary<string, string>
        {
            [ProjectedSpecConsole.EnvironmentVariable] = "1",
            [ProjectedSpecConsole.WidthEnvironmentVariable] = SpecHost.Width().ToString(),
            [ResultsFile.EnvironmentVariable] = results
        };
        if (options.Verbose) environment[Engine.ScenarioReportVisibility.EnvironmentVariable] = "1";

        var launched = 0;
        var factory = new MtpWorkerFactory(project.HostPath, environment)
        {
            EnvironmentFor = context => new Dictionary<string, string>
            {
                [ProjectedSpecConsole.OutputFileEnvironmentVariable] = Path.Combine(renders,
                    $"{Interlocked.Increment(ref launched):D4}-{context.Purpose}-lane{context.Lane}.ansi")
            }
        };

        var gate = new object();
        await using var logWriter = new StreamWriter(log, append: false) { AutoFlush = true };

        var supervisor = new Supervisor.Supervisor(factory)
        {
            RetryBudget = supervision.Budget,
            MaxParallelWorkers = supervision.Workers,
            StallThreshold = supervision.Stall,
            StallAction = supervision.StallAction,
            Specifications = SpecSelection.Of(identities),

            // The supervisor owns the run bracket and every worker publishes under it, so a console
            // sees one run rather than one per worker. Nobody answering costs a probe and nothing else.
            PublishToMonitor = true,
            Log = line =>
            {
                lock (gate) logWriter.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
            }
        };

        var started = DateTime.UtcNow;
        var lanes = supervision.Workers == 1 ? "1 worker" : $"{supervision.Workers} workers";
        var outcome = await AnsiConsole.Status().StartAsync(
            $"Running {project.Name} supervised ({lanes}, retries {supervision.Retries}, stall {describe(supervision)})…",
            _ => supervisor.Run(token));

        foreach (var file in Directory.EnumerateFiles(renders, "*.ansi").Order(StringComparer.Ordinal))
        {
            System.Console.Write(File.ReadAllText(file));
        }

        System.Console.WriteLine();
        System.Console.WriteLine(RunReport.ToText(outcome));
        System.Console.WriteLine();

        var written = SpecHost.WrittenSince(results, started);
        if (!ownResults)
        {
            foreach (var file in written) AnsiConsole.MarkupLine($"[dim]results: {Markup.Escape(file)}[/]");
        }

        // Every worker's file on one page; a retried specification is there once per attempt.
        if (options.HtmlFile is { } html) SpecHost.WriteHtml(html, written, attachmentsKept: !ownResults);

        IReadOnlyList<string> failed = [];
        var resultsRead = false;
        try
        {
            failed = LastRun.FailedIn(written);
            resultsRead = written.Count > 0;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            // An unreadable results file leaves the run's verdicts unknown, never the run failed
        }

        foreach (var directory in ownResults ? [results, renders] : new[] { renders })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* a temp directory is not worth failing a run over */ }
        }

        var elapsed = DateTime.UtcNow - started;
        AnsiConsole.MarkupLine(
            $"{verdict(outcome)} [dim]in {elapsed.TotalSeconds:0.0}s · supervisor log: {Markup.Escape(log)}[/]");

        return new RunOutcome(outcome.ExitCode, failed, resultsRead);
    }

    private static string describe(Supervision supervision)
        => $"{(int)supervision.Stall.TotalSeconds}s, {Supervision.Spell(supervision.StallAction)}";

    // SupervisorResults.ExitCode: 0 all passed (on retry included), 1 a test failed, 2 aborted or unknown
    private static string verdict(SupervisorResults results) => results.ExitCode switch
    {
        0 when results.Tests.Count == 0 => "[yellow]Nothing ran[/]",
        0 when results.PassedOnRetry.Count > 0 => $"[green]All green[/] [yellow]({results.PassedOnRetry.Count} on retry)[/]",
        0 => "[green]All green[/]",
        1 => "[red]Specifications failed[/]",
        _ when results.AbortReason is not null => $"[red]The run was aborted:[/] {Markup.Escape(results.AbortReason)}",
        _ => "[red]Some results were never established[/] [dim](a worker died; see the report above)[/]"
    };
}
