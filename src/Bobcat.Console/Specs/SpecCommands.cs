using JasperFx.CommandLine;
using Spectre.Console;

namespace Bobcat.Console.Specs;

public class SpecInput
{
    [Description("The spec project: a .csproj, or a directory holding one. Found from the current directory when omitted")]
    public string Project { get; set; } = "";

    [Description("The target framework to run; the newest the project targets by default")]
    [FlagAlias("framework", 'f')]
    public string? FrameworkFlag { get; set; }

    [Description("The build configuration")]
    [FlagAlias("configuration", 'c')]
    public string ConfigurationFlag { get; set; } = "Debug";

    [Description("Only the features whose title contains this (case-insensitive); repeatable")]
    public IEnumerable<string> FeatureFlag { get; set; } = [];

    [Description("Only the scenarios whose title contains this (case-insensitive); repeatable")]
    public IEnumerable<string> ScenarioFlag { get; set; } = [];

    [Description("Only the specifications bound to this Event Model slice; repeatable (a repeated flag ORs, different flags AND)")]
    public IEnumerable<string> SliceFlag { get; set; } = [];

    [Description("Only the specifications whose slice is in this chapter; repeatable")]
    public IEnumerable<string> ChapterFlag { get; set; } = [];

    [Description("Only the specifications whose slice is in this domain; repeatable")]
    public IEnumerable<string> DomainFlag { get; set; } = [];

    [Description("Only the specifications whose slice has this pattern (Command, View, Query, Automation, Translation); repeatable")]
    public IEnumerable<string> PatternFlag { get; set; } = [];

    [Description("Only the specifications with this trait, Name=Value (Slice/Chapter/Domain/Pattern included); repeatable")]
    public IEnumerable<string> TraitFlag { get; set; } = [];

    [Description("Use the last build as it is")]
    public bool NoBuildFlag { get; set; }

    [Description("Write the run's JSON results file (and its schema) into this directory -- BOBCAT_RESULTS for the host")]
    public string? ResultsFlag { get; set; }

    [Description("Print the run's JSON results file on standard output instead of the rendered specifications; everything else goes to standard error")]
    public bool JsonFlag { get; set; }

    [Description("Write every scenario's reports, passing ones too, on every surface -- BOBCAT_VERBOSE_REPORTS for the host")]
    public bool VerboseFlag { get; set; }

    [Description("Also write the run as one self-contained HTML results page to this file; with --results its attachments link from it")]
    public string? HtmlFlag { get; set; }

    internal RunOptions RunOptions => new(
        ResultsFlag is { Length: > 0 } results ? Path.GetFullPath(results) : null, JsonFlag, VerboseFlag,
        HtmlFlag is { Length: > 0 } html ? Path.GetFullPath(html) : null);

    /// <summary>
    /// With <c>--json</c>, standard output is the results file and nothing else, so an agent can
    /// pipe it straight into a parser: every other line the tool writes goes to standard error.
    /// </summary>
    internal void RouteConsole()
    {
        if (!JsonFlag) return;

        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(System.Console.Error)
        });
    }

    internal Task<SpecProject> ProjectAsync()
        => SpecProject.ResolveAsync(Project.Length == 0 ? null : Project, FrameworkFlag, ConfigurationFlag);
}

public class SpecRunInput : SpecInput
{
    [Description("Run only the specifications that failed or errored in this project's last run (obj/bobcat/last-run.json)")]
    public bool FailedFlag { get; set; }

    [Description("Run the last run's selection again, with its framework, configuration, --results, --html, --json, --verbose and supervision flags")]
    public bool RepeatFlag { get; set; }

    [Description("Supervised: retry a failed specification up to N times. Only one with a Retry trait or a recovery hint is retried")]
    public int? RetriesFlag { get; set; }

    [Description("Supervised: run on N worker processes, partitioned by test class")]
    public int? WorkersFlag { get; set; }

    [Description("Supervised: name a specification in flight longer than this (90s, 5m, 1h); 5m by default")]
    public string? StallFlag { get; set; }

    [Description("Supervised: what a stall does -- report (the default), kill-and-retry, or abort")]
    public string? StallActionFlag { get; set; }

    /// <summary>The supervision the flags ask for; null for a plain run.</summary>
    internal Supervision? Supervision(out string? refusal)
        => Specs.Supervision.From(RetriesFlag, WorkersFlag, StallFlag, StallActionFlag, out refusal);
}

/// <summary>
/// <c>bobcat run</c> — run a spec project and read its specifications, not its log: the test host's
/// output goes to a file and the terminal gets the rendered Feature / scenario / step tree.
/// </summary>
/// <remarks>
/// <para>
/// <c>--retries</c>, <c>--workers</c>, <c>--stall</c> or <c>--stall-action</c> make the run supervised
/// (bobcat#546, <see cref="SupervisedHost"/>): the same host, driven as worker processes by the
/// supervisor. Without any of them the run is exactly the plain one.
/// </para>
/// Every run leaves <c>obj/bobcat/last-run.json</c> behind (<see cref="LastRun"/>, bobcat#572):
/// what it asked for and what failed. <c>--failed</c> runs the failures again by identity, and
/// <c>--repeat</c> replays the selection and flags. An identity the suite no longer specifies is
/// refused by name rather than dropped, because a narrowed run that matches nothing exits 0.
/// </remarks>
[Description("Run a spec project and show its specifications", Name = "run")]
public class SpecRunCommand : JasperFxAsyncCommand<SpecRunInput>
{
    public SpecRunCommand()
    {
        Usage("Run the spec project found from the current directory").NoArguments();
        Usage("Run a spec project").Arguments(x => x.Project);
    }

    public override async Task<bool> Execute(SpecRunInput input)
    {
        input.RouteConsole();

        if (input.FailedFlag && input.RepeatFlag)
        {
            AnsiConsole.MarkupLine("[red]--failed and --repeat each choose what to run; use one.[/]");
            return false;
        }

        if ((input.FailedFlag || input.RepeatFlag)
            && (input.FeatureFlag.Any() || input.ScenarioFlag.Any() || SpecSelectionFlow.CoordinatesOf(input).NarrowsAnything))
        {
            AnsiConsole.MarkupLine($"[red]--{(input.FailedFlag ? "failed" : "repeat")} chooses what to run from the last run; "
                                   + "it cannot be combined with --feature, --scenario, --slice, --chapter, --domain, --pattern or --trait.[/]");
            return false;
        }

        var supervision = input.Supervision(out var refusal);
        if (refusal is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(refusal)}[/]");
            return false;
        }

        var project = await input.ProjectAsync();

        LastRun? last = null;
        if (input.FailedFlag || input.RepeatFlag)
        {
            last = LastRun.Read(LastRun.PathFor(project));
            if (last is null)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(project.Name)} has no last run to go back to[/] "
                                       + $"[dim]({Markup.Escape(LastRun.PathFor(project))} is missing or unreadable). Run `bobcat run` first.[/]");
                return false;
            }
        }

        if (input.RepeatFlag)
        {
            var recorded = last!.Arguments;
            input.FrameworkFlag ??= recorded.Framework;
            input.ConfigurationFlag = recorded.Configuration;
            input.ResultsFlag ??= recorded.Results;
            input.HtmlFlag ??= recorded.Html;
            input.JsonFlag |= recorded.Json;
            input.VerboseFlag |= recorded.Verbose;
            input.RetriesFlag ??= recorded.Retries;
            input.WorkersFlag ??= recorded.Workers;
            input.StallFlag ??= recorded.Stall;
            input.StallActionFlag ??= recorded.StallAction;
            input.RouteConsole();

            project = await input.ProjectAsync();
        }

        // Again, for the flags --repeat replayed; what it recorded was valid when it ran.
        supervision = input.Supervision(out _);
        if (supervision is not null && input.JsonFlag)
        {
            // A supervised run writes a results file per worker request, so there is no one file
            // to put on standard output; they all go to --results.
            AnsiConsole.MarkupLine("[red]--json prints one results file, and a supervised run (--retries, --workers, --stall, "
                                   + "--stall-action) writes one per worker. Use --results <dir> to keep them all.[/]");
            return false;
        }

        IReadOnlyList<string>? selection;
        if (input.FailedFlag)
        {
            if (!last!.ResultsRead)
            {
                AnsiConsole.MarkupLine($"[red]The last run of {Markup.Escape(project.Name)} left no results[/] "
                                       + $"[dim](the test host exited {last.ExitCode} before reporting), so what failed is not known. "
                                       + "Run it again without --failed.[/]");
                return false;
            }

            if (last.Failed.Count == 0)
            {
                var exited = last.ExitCode == 0 ? "" : $", though the test host exited {last.ExitCode}";
                AnsiConsole.MarkupLine($"[green]Nothing failed in the last run of {Markup.Escape(project.Name)}[/] "
                                       + $"[dim]({last.Timestamp.ToLocalTime():g}{Markup.Escape(exited)}), so there is nothing to rerun.[/]");
                return true;
            }

            selection = last.Failed;
        }
        else if (input.RepeatFlag)
        {
            selection = last!.Arguments.Selection;
        }
        else
        {
            selection = null;
        }

        if (!await project.BuildAsync(input.NoBuildFlag)) return false;

        var chosen = selection is null
            ? await SpecSelectionFlow.ChooseAsync(project, input)
            : await SpecSelectionFlow.IdentitiesAsync(project, selection);
        if (chosen is null) return false;

        var outcome = supervision is null
            ? await SpecHost.RunAsync(project, chosen.FilterArguments, input.RunOptions)
            : await SupervisedHost.RunAsync(project, chosen.Identities, input.RunOptions, supervision);

        remember(project, input, chosen, outcome, supervision);

        return outcome.ExitCode == 0;
    }

    /// <summary>Write <c>obj/bobcat/last-run.json</c>. Never fails the run it describes.</summary>
    private static void remember(SpecProject project, SpecRunInput input, Chosen chosen, RunOutcome outcome,
        Supervision? supervision)
    {
        var path = LastRun.PathFor(project);
        try
        {
            new LastRun
            {
                Timestamp = DateTimeOffset.UtcNow,
                ExitCode = outcome.ExitCode,
                ResultsRead = outcome.ResultsRead,
                Failed = outcome.Failed.ToList(),
                Arguments = new LastRunArguments
                {
                    Framework = project.Framework,
                    Configuration = project.Configuration,
                    Selection = chosen.Identities.ToList(),
                    Results = input.RunOptions.ResultsDirectory,
                    Html = input.RunOptions.HtmlFile,
                    Json = input.JsonFlag,
                    Verbose = input.VerboseFlag,
                    Retries = supervision?.Retries,
                    Workers = supervision?.Workers,
                    Stall = supervision is null ? null : input.StallFlag,
                    StallAction = supervision is null ? null : Specs.Supervision.Spell(supervision.StallAction)
                }
            }.Write(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[dim]Could not write {Markup.Escape(path)}: {Markup.Escape(e.Message)}[/]");
        }
    }
}

/// <summary><c>bobcat preview</c> — what a spec project specifies, without running it.</summary>
[Description("Show a spec project's specifications without running them", Name = "preview")]
public class SpecPreviewCommand : JasperFxAsyncCommand<SpecInput>
{
    public SpecPreviewCommand()
    {
        Usage("Preview the spec project found from the current directory").NoArguments();
        Usage("Preview a spec project").Arguments(x => x.Project);
    }

    public override async Task<bool> Execute(SpecInput input)
    {
        var project = await input.ProjectAsync();
        if (!await project.BuildAsync(input.NoBuildFlag)) return false;

        return await SpecHost.PreviewAsync(project) == 0;
    }
}

/// <summary><c>bobcat pick</c> — choose features and scenarios from a menu, then run them.</summary>
[Description("Choose specifications from a menu and run them", Name = "pick")]
public class SpecPickCommand : JasperFxAsyncCommand<SpecInput>
{
    public SpecPickCommand()
    {
        Usage("Pick from the spec project found from the current directory").NoArguments();
        Usage("Pick from a spec project").Arguments(x => x.Project);
    }

    public override async Task<bool> Execute(SpecInput input)
    {
        input.RouteConsole();

        var project = await input.ProjectAsync();
        if (!await project.BuildAsync(input.NoBuildFlag)) return false;

        var filter = await SpecSelectionFlow.PickAsync(project);
        if (filter is null) return false;

        return (await SpecHost.RunAsync(project, filter, input.RunOptions)).ExitCode == 0;
    }
}

/// <summary>
/// <c>bobcat watch</c> — run, then rerun whenever the spec project or anything it references
/// changes. Enter reruns, <c>p</c> picks a new selection, <c>a</c> goes back to everything,
/// <c>q</c> quits.
/// </summary>
[Description("Run a spec project, and again whenever its source changes", Name = "watch")]
public class SpecWatchCommand : JasperFxAsyncCommand<SpecInput>
{
    public SpecWatchCommand()
    {
        Usage("Watch the spec project found from the current directory").NoArguments();
        Usage("Watch a spec project").Arguments(x => x.Project);
    }

    public override async Task<bool> Execute(SpecInput input)
    {
        var project = await input.ProjectAsync();
        if (!await project.BuildAsync(input.NoBuildFlag)) return false;

        var chosen = await SpecSelectionFlow.ChooseAsync(project, input);
        if (chosen is null) return false;
        var filter = chosen.FilterArguments;

        using var changes = new SourceWatcher(project.SourceDirectories());
        using var quit = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Cancel();
        };

        var interactive = !System.Console.IsInputRedirected;
        var buildFailed = false;

        while (!quit.IsCancellationRequested)
        {
            // A failed rebuild leaves the previous host on disk; running it would report on code
            // that no longer exists, so the loop waits for the next change instead.
            if (!buildFailed) await SpecHost.RunAsync(project, filter, input.RunOptions with { Json = false });
            buildFailed = false;

            AnsiConsole.MarkupLine(interactive
                ? "[dim]Watching for changes · Enter reruns · p picks · a runs everything · q quits[/]"
                : "[dim]Watching for changes · Ctrl+C quits[/]");

            var next = await nextAsync(changes, interactive, quit.Token);
            switch (next)
            {
                case Next.Quit:
                    return true;
                case Next.Pick:
                    filter = await SpecSelectionFlow.PickAsync(project) ?? filter;
                    break;
                case Next.Everything:
                    filter = [];
                    break;
            }

            if (next == Next.Changed || next == Next.Pick || next == Next.Everything || next == Next.Rerun)
            {
                AnsiConsole.Write(new Rule($"[dim]{DateTime.Now:T} · {(next == Next.Changed ? "source changed" : "rerun")}[/]"));
                if (next == Next.Changed && !await project.BuildAsync()) buildFailed = true;
            }
        }

        return true;
    }

    private enum Next { Changed, Rerun, Pick, Everything, Quit }

    private static async Task<Next> nextAsync(SourceWatcher changes, bool interactive, CancellationToken token)
    {
        changes.Reset();
        while (!token.IsCancellationRequested)
        {
            if (changes.HasChangedSinceReset(TimeSpan.FromMilliseconds(600))) return Next.Changed;

            if (interactive && System.Console.KeyAvailable)
            {
                switch (System.Console.ReadKey(intercept: true).Key)
                {
                    case ConsoleKey.Enter: return Next.Rerun;
                    case ConsoleKey.P: return Next.Pick;
                    case ConsoleKey.A: return Next.Everything;
                    case ConsoleKey.Q or ConsoleKey.Escape: return Next.Quit;
                }
            }

            try
            {
                await Task.Delay(150, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return Next.Quit;
    }
}

/// <summary>
/// The identities a run was narrowed to (none: every specification) and the framework filter that
/// selects them.
/// </summary>
internal sealed record Chosen(IReadOnlyList<string> Identities, IReadOnlyList<string> FilterArguments)
{
    public static Chosen Everything { get; } = new([], []);
}

/// <summary>Turning flags or a menu into a framework filter, against the suite's own manifest.</summary>
internal static class SpecSelectionFlow
{
    public static async Task<Chosen?> ChooseAsync(SpecProject project, SpecInput input)
    {
        var features = input.FeatureFlag.ToList();
        var scenarios = input.ScenarioFlag.ToList();
        var coordinates = CoordinatesOf(input);
        if (features.Count == 0 && scenarios.Count == 0 && !coordinates.NarrowsAnything) return Chosen.Everything;

        var manifest = await SpecHost.ListAsync(project);
        var identities = SpecHost.Choose(manifest, features, scenarios, coordinates);
        return identities is null
            ? null
            : new Chosen(identities, Bobcat.Runtime.SpecFilterArguments.For(manifest, Bobcat.Runtime.SpecSelection.Of(identities)));
    }

    /// <summary>
    /// Exactly <paramref name="identities"/> (none: every specification), or null when the suite no
    /// longer specifies one of them — named, rather than dropped from the run, because a rerun of
    /// three failures that silently ran two looks exactly like a pass.
    /// </summary>
    public static async Task<Chosen?> IdentitiesAsync(SpecProject project, IReadOnlyList<string> identities)
    {
        if (identities.Count == 0) return Chosen.Everything;

        var manifest = await SpecHost.ListAsync(project);
        var selection = Bobcat.Runtime.SpecSelection.Of(identities);

        var unknown = selection.NotIn(manifest.Identities);
        if (unknown.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(project.Name)} no longer specifies {unknown.Count} of the last run's specification(s):[/]");
            foreach (var identity in unknown) AnsiConsole.MarkupLine($"  {Markup.Escape(identity)}");
            AnsiConsole.MarkupLine("[dim]Renamed or deleted since the last run. Run `bobcat run` (with --feature / --scenario) to start over.[/]");
            return null;
        }

        try
        {
            return new Chosen(selection.Identities, Bobcat.Runtime.SpecFilterArguments.For(manifest, selection));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            AnsiConsole.MarkupLine($"[red]Cannot run exactly those specifications:[/] {Markup.Escape(e.Message)}");
            return null;
        }
    }

    /// <summary>The Event Model coordinate and trait flags (bobcat#573) as one filter.</summary>
    public static Bobcat.Runtime.SpecCoordinateFilter CoordinatesOf(SpecInput input) => new()
    {
        Slices = input.SliceFlag.ToList(),
        Chapters = input.ChapterFlag.ToList(),
        Domains = input.DomainFlag.ToList(),
        Patterns = input.PatternFlag.ToList(),
        Traits = input.TraitFlag.ToList()
    };

    public static async Task<IReadOnlyList<string>?> PickAsync(SpecProject project)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[red]Picking needs an interactive terminal.[/] Use --feature or --scenario instead.");
            return null;
        }

        var manifest = await SpecHost.ListAsync(project);
        if (manifest.Identities.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(project.Name)} specifies nothing to pick from.[/]");
            return null;
        }

        var prompt = new MultiSelectionPrompt<string>()
            .Title($"Which specifications in [bold]{Markup.Escape(project.Name)}[/]?")
            .PageSize(20)
            .InstructionsText("[dim](space toggles a scenario, or a whole feature on its heading · enter runs)[/]")
            .UseConverter(identity => Markup.Escape(identity.Contains('/') ? SpecHost.Split(identity).Scenario : identity));

        foreach (var feature in manifest.Identities.GroupBy(x => SpecHost.Split(x).Feature).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            prompt.AddChoiceGroup(feature.Key, feature.OrderBy(x => x, StringComparer.Ordinal));
        }

        // A group's heading is itself a choice; it stands for its feature and is not an identity
        var chosen = AnsiConsole.Prompt(prompt).Where(manifest.Identities.Contains).ToList();
        if (chosen.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing chosen.[/]");
            return null;
        }

        return Bobcat.Runtime.SpecFilterArguments.For(manifest, Bobcat.Runtime.SpecSelection.Of(chosen));
    }
}

/// <summary>Source changes under a set of directories, settled for a moment before they count.</summary>
internal sealed class SourceWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private DateTime? _lastChange;
    private readonly object _gate = new();

    public SourceWatcher(IEnumerable<string> directories)
    {
        foreach (var directory in directories.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
            };

            watcher.Changed += onChange;
            watcher.Created += onChange;
            watcher.Deleted += onChange;
            watcher.Renamed += onChange;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public void Reset()
    {
        lock (_gate) _lastChange = null;
    }

    /// <summary>A change happened, and nothing has changed for <paramref name="settle"/> since — a save of several files is one rerun.</summary>
    public bool HasChangedSinceReset(TimeSpan settle)
    {
        lock (_gate) return _lastChange is { } last && DateTime.UtcNow - last >= settle;
    }

    private void onChange(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        var parts = path.Split(Path.DirectorySeparatorChar);
        if (parts.Any(p => p is "bin" or "obj" or ".git" or ".vs" or ".idea")) return;
        if (!(path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
              || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
              || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) return;

        lock (_gate) _lastChange = DateTime.UtcNow;
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
    }
}
