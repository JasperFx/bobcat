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

    [Description("Use the last build as it is")]
    public bool NoBuildFlag { get; set; }

    internal Task<SpecProject> ProjectAsync()
        => SpecProject.ResolveAsync(Project.Length == 0 ? null : Project, FrameworkFlag, ConfigurationFlag);
}

/// <summary>
/// <c>bobcat run</c> — run a spec project and read its specifications, not its log: the test host's
/// output goes to a file and the terminal gets the rendered Feature / scenario / step tree.
/// </summary>
[Description("Run a spec project and show its specifications", Name = "run")]
public class SpecRunCommand : JasperFxAsyncCommand<SpecInput>
{
    public SpecRunCommand()
    {
        Usage("Run the spec project found from the current directory").NoArguments();
        Usage("Run a spec project").Arguments(x => x.Project);
    }

    public override async Task<bool> Execute(SpecInput input)
    {
        var project = await input.ProjectAsync();
        if (!await project.BuildAsync(input.NoBuildFlag)) return false;

        var filter = await SpecSelectionFlow.FilterAsync(project, input);
        if (filter is null) return false;

        return await SpecHost.RunAsync(project, filter) == 0;
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
        var project = await input.ProjectAsync();
        if (!await project.BuildAsync(input.NoBuildFlag)) return false;

        var filter = await SpecSelectionFlow.PickAsync(project);
        if (filter is null) return false;

        return await SpecHost.RunAsync(project, filter) == 0;
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

        var filter = await SpecSelectionFlow.FilterAsync(project, input);
        if (filter is null) return false;

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
            if (!buildFailed) await SpecHost.RunAsync(project, filter);
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

/// <summary>Turning flags or a menu into a framework filter, against the suite's own manifest.</summary>
internal static class SpecSelectionFlow
{
    public static async Task<IReadOnlyList<string>?> FilterAsync(SpecProject project, SpecInput input)
    {
        var features = input.FeatureFlag.ToList();
        var scenarios = input.ScenarioFlag.ToList();
        if (features.Count == 0 && scenarios.Count == 0) return [];

        return SpecHost.Filter(await SpecHost.ListAsync(project), features, scenarios);
    }

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
              || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
              || path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))) return;

        lock (_gate) _lastChange = DateTime.UtcNow;
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
    }
}
