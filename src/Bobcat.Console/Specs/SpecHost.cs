using Bobcat.Residency;
using Bobcat.Runtime;
using Spectre.Console;

namespace Bobcat.Console.Specs;

/// <summary>
/// One launch of a spec project's test host with the specification rendering switched on and
/// pointed at a file, so the host's own logging goes to a log file and the terminal shows the
/// specifications — then the run's verdict and where the log is.
/// </summary>
internal static class SpecHost
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "bobcat-tool");

    /// <summary>Run the host — every specification, or only <paramref name="selection"/>.</summary>
    public static async Task<int> RunAsync(SpecProject project, IReadOnlyList<string> filterArguments, CancellationToken token = default)
    {
        Directory.CreateDirectory(Workspace);
        var render = Path.Combine(Workspace, $"{project.Name}.specs.ansi");
        var log = Path.Combine(Workspace, $"{project.Name}.log");
        File.Delete(render);

        var environment = new Dictionary<string, string?>
        {
            [ProjectedSpecConsole.EnvironmentVariable] = "1",
            [ProjectedSpecConsole.OutputFileEnvironmentVariable] = render,
            [ProjectedSpecConsole.WidthEnvironmentVariable] = width().ToString()
        };

        var started = DateTime.UtcNow;
        var (code, _) = await AnsiConsole.Status().StartAsync(
            filterArguments.Count == 0 ? $"Running {project.Name}…" : $"Running the selected specifications in {project.Name}…",
            _ => Shell.RunAsync(project.HostPath, filterArguments, environment, log, token));

        var rendered = File.Exists(render) ? File.ReadAllText(render) : "";
        if (rendered.Length > 0)
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

        var elapsed = DateTime.UtcNow - started;
        AnsiConsole.MarkupLine(
            $"{verdict(code)} [dim]in {elapsed.TotalSeconds:0.0}s · host log: {Markup.Escape(log)}[/]");

        return code;
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

        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(project.Name)} previewed nothing (exit {code}). The end of its output:[/]");
        foreach (var line in File.ReadLines(log).TakeLast(30)) System.Console.WriteLine(line);
        return code == 0 ? 1 : code;
    }

    /// <summary>What the project specifies, from the manifest its generator registers.</summary>
    public static Task<SpecManifest> ListAsync(SpecProject project, CancellationToken token = default)
        => AnsiConsole.Status().StartAsync($"Listing {project.Name}…",
            _ => OutOfProcessResidentSuite.Listing(project.HostPath, token: token));

    /// <summary>
    /// The framework filter for the specifications whose feature or scenario title contains one of
    /// <paramref name="features"/> / <paramref name="scenarios"/>, case-insensitively — or no filter
    /// at all when neither narrows anything. Null when they match nothing, which is reported rather
    /// than run, because a filter that matches nothing runs nothing and reads as green.
    /// </summary>
    public static IReadOnlyList<string>? Filter(SpecManifest manifest, IReadOnlyList<string> features, IReadOnlyList<string> scenarios)
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

        return SpecFilterArguments.For(manifest, SpecSelection.Of(chosen));
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
