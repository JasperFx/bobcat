using Bobcat.Rendering;
using Bobcat.Residency;
using Bobcat.Runtime;
using Spectre.Console;

namespace Bobcat.Console.Specs;

/// <summary>
/// How a run reports beyond the terminal (issue #540): a results directory for the JSON results
/// file, the file itself on standard output, and whether passing scenarios' reports are written.
/// </summary>
internal record RunOptions(string? ResultsDirectory = null, bool Json = false, bool Verbose = false)
{
    public static RunOptions Default { get; } = new();
}

/// <summary>
/// A run's exit code and, read from its results file, the specifications that did not succeed.
/// <see cref="ResultsRead"/> is false when the host wrote no results file, and then
/// <see cref="Failed"/> says nothing.
/// </summary>
internal record RunOutcome(int ExitCode, IReadOnlyList<string> Failed, bool ResultsRead);

/// <summary>
/// One launch of a spec project's test host with the specification rendering switched on and
/// pointed at a file, so the host's own logging goes to a log file and the terminal shows the
/// specifications — then the run's verdict and where the log is.
/// </summary>
internal static class SpecHost
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "bobcat-tool");

    /// <summary>Run the host — every specification, or only <paramref name="selection"/>.</summary>
    public static async Task<RunOutcome> RunAsync(SpecProject project, IReadOnlyList<string> filterArguments,
        RunOptions? options = null, CancellationToken token = default)
    {
        options ??= RunOptions.Default;

        Directory.CreateDirectory(Workspace);
        var render = Path.Combine(Workspace, $"{project.Name}.specs.ansi");
        var log = Path.Combine(Workspace, $"{project.Name}.log");
        File.Delete(render);

        // Every run writes a results file, because that is where `--failed` reads what failed
        // (bobcat#572). With no --results it goes to a directory of this run's own, deleted
        // afterwards: another run of the same project on this machine must not be read as this one.
        var ownResults = options.ResultsDirectory is null;
        var results = options.ResultsDirectory
                      ?? Path.Combine(Workspace, "results", project.Name, Guid.NewGuid().ToString("n"));

        var environment = new Dictionary<string, string?>
        {
            [ProjectedSpecConsole.WidthEnvironmentVariable] = width().ToString()
        };

        if (options.Json)
        {
            // The file is the output; a rendering nobody prints would only cost the host time.
            environment[ProjectedSpecConsole.EnvironmentVariable] = "0";
        }
        else
        {
            environment[ProjectedSpecConsole.EnvironmentVariable] = "1";
            environment[ProjectedSpecConsole.OutputFileEnvironmentVariable] = render;
        }

        environment[ResultsFile.EnvironmentVariable] = results;
        if (options.Verbose) environment[Engine.ScenarioReportVisibility.EnvironmentVariable] = "1";

        var started = DateTime.UtcNow;
        var (code, _) = await AnsiConsole.Status().StartAsync(
            filterArguments.Count == 0 ? $"Running {project.Name}…" : $"Running the selected specifications in {project.Name}…",
            _ => Shell.RunAsync(project.HostPath, filterArguments, environment, log, token));

        var written = WrittenSince(results, started);
        var rendered = File.Exists(render) ? File.ReadAllText(render) : "";

        if (options.Json)
        {
            if (written.Count > 0)
            {
                // The newest: a one-shot host writes exactly one, and a host that somehow wrote
                // several (one per run request) finished its last run last.
                System.Console.Out.Write(File.ReadAllText(written[^1]));
                System.Console.Out.WriteLine();
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No results file was written. The end of the host's output:[/]");
                foreach (var line in File.ReadLines(log).TakeLast(30)) AnsiConsole.Console.WriteLine(line);
            }
        }
        else if (rendered.Length > 0)
        {
            System.Console.Write(rendered);
        }
        else
        {
            // Nothing rendered: the host failed before a scenario closed, or nothing it ran is a
            // specification. Its own last words are the explanation either way.
            AnsiConsole.MarkupLine("[yellow]No specifications were rendered. The end of the host's output:[/]");
            foreach (var line in File.ReadLines(log).TakeLast(30)) System.Console.WriteLine(line);
        }

        if (!options.Json && !ownResults)
        {
            foreach (var file in written) AnsiConsole.MarkupLine($"[dim]results: {Markup.Escape(file)}[/]");
        }

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

        if (ownResults)
        {
            try { Directory.Delete(results, recursive: true); }
            catch { /* a temp directory is not worth failing a run over */ }
        }

        var elapsed = DateTime.UtcNow - started;
        AnsiConsole.MarkupLine(
            $"{verdict(code)} [dim]in {elapsed.TotalSeconds:0.0}s · host log: {Markup.Escape(log)}[/]");

        return new RunOutcome(code, failed, resultsRead);
    }

    /// <summary>
    /// The results files in <paramref name="directory"/> written since <paramref name="since"/>,
    /// oldest first — the run's own, never an earlier run's left in the same directory.
    /// </summary>
    internal static IReadOnlyList<string> WrittenSince(string directory, DateTime since)
    {
        if (!Directory.Exists(directory)) return [];

        // A second of slack for filesystems that keep coarse timestamps.
        var threshold = since.AddSeconds(-1);

        return new DirectoryInfo(directory).EnumerateFiles("*.json")
            .Where(x => x.Name != ResultsFile.SchemaFileName && x.LastWriteTimeUtc >= threshold)
            .OrderBy(x => x.LastWriteTimeUtc)
            .Select(x => x.FullName)
            .ToList();
    }

    /// <summary>Show what the project specifies, without running any of it.</summary>
    public static async Task<int> PreviewAsync(SpecProject project, CancellationToken token = default)
    {
        Directory.CreateDirectory(Workspace);
        var render = Path.Combine(Workspace, $"{project.Name}.preview.ansi");
        var log = Path.Combine(Workspace, $"{project.Name}.preview.log");
        File.Delete(render);

        var environment = new Dictionary<string, string?>
        {
            [ProjectedSpecConsole.PreviewEnvironmentVariable] = "1",
            [ProjectedSpecConsole.OutputFileEnvironmentVariable] = render,
            [ProjectedSpecConsole.WidthEnvironmentVariable] = width().ToString(),
            ["BOBCAT_MONITOR"] = "0"
        };

        var (code, _) = await Shell.RunAsync(project.HostPath, ["--list-tests"], environment, log, token);

        if (File.Exists(render) && new FileInfo(render).Length > 0)
        {
            System.Console.Write(File.ReadAllText(render));
            return 0;
        }

        // No step is known before a run — a suite whose steps are recorded as it runs (a library's
        // Given/When/Then helpers, rather than marker comments or [BobcatStep] calls). Its manifest
        // still names every specification, so the preview is the outline.
        SpecManifest? manifest = null;
        try
        {
            manifest = await OutOfProcessResidentSuite.Listing(project.HostPath, token: token);
        }
        catch (InvalidOperationException)
        {
            // Not a projected suite at all; the host's own output below says why
        }

        if (manifest is { Identities.Count: > 0 })
        {
            foreach (var feature in manifest.Identities.GroupBy(x => Split(x).Feature).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[bold]Feature: {Markup.Escape(feature.Key)}[/]");
                foreach (var identity in feature.OrderBy(x => x, StringComparer.Ordinal))
                {
                    AnsiConsole.MarkupLine($"  [dim]○[/] {Markup.Escape(Split(identity).Scenario)}");
                }
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[bold]{manifest.Identities.Count} specification(s)[/] [dim]— their steps are recorded as they run, so only "
                + "scenarios can be shown before a run. Marker comments (// Given …) declare steps a preview can show.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(project.Name)} previewed nothing (exit {code}). The end of its output:[/]");
        foreach (var line in File.ReadLines(log).TakeLast(30)) System.Console.WriteLine(line);
        return code == 0 ? 1 : code;
    }

    /// <summary>What the project specifies, from the manifest its generator registers.</summary>
    public static Task<SpecManifest> ListAsync(SpecProject project, CancellationToken token = default)
        => AnsiConsole.Status().StartAsync($"Listing {project.Name}…",
            _ => OutOfProcessResidentSuite.Listing(project.HostPath, token: token));

    /// <summary>
    /// The identities of the specifications whose feature or scenario title contains one of
    /// <paramref name="features"/> / <paramref name="scenarios"/>, case-insensitively — or none at
    /// all, meaning every specification, when neither narrows anything. Null when they match
    /// nothing, which is reported rather than run, because a filter that matches nothing runs
    /// nothing and reads as green.
    /// </summary>
    public static IReadOnlyList<string>? Choose(SpecManifest manifest, IReadOnlyList<string> features, IReadOnlyList<string> scenarios)
    {
        if (features.Count == 0 && scenarios.Count == 0) return [];

        var chosen = manifest.Identities.Where(identity =>
        {
            var (feature, scenario) = Split(identity);
            return (features.Count == 0 || features.Any(f => feature.Contains(f, StringComparison.OrdinalIgnoreCase)))
                   && (scenarios.Count == 0 || scenarios.Any(s => scenario.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }).ToList();

        if (chosen.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No specification matches.[/] The features are:");
            foreach (var feature in manifest.Identities.Select(x => Split(x).Feature).Distinct().Order())
            {
                AnsiConsole.MarkupLine($"  {Markup.Escape(feature)}");
            }

            return null;
        }

        return chosen;
    }

    /// <summary>An identity is <c>{Feature}/{Scenario}</c>; the scenario never contains the separator's first occurrence.</summary>
    public static (string Feature, string Scenario) Split(string identity)
    {
        var slash = identity.IndexOf('/');
        return slash < 0 ? ("", identity) : (identity[..slash], identity[(slash + 1)..]);
    }

    private static int width()
    {
        try
        {
            return System.Console.IsOutputRedirected ? 140 : Math.Max(80, System.Console.WindowWidth);
        }
        catch
        {
            return 140;
        }
    }

    // Microsoft Testing Platform exit codes: 0 all passed, 2 a test failed, 8 nothing ran
    private static string verdict(int code) => code switch
    {
        0 => "[green]All green[/]",
        2 => "[red]Specifications failed[/]",
        8 => "[yellow]Nothing ran[/] [dim](the filter matched no tests)[/]",
        _ => $"[red]The test host exited {code}[/]"
    };
}
