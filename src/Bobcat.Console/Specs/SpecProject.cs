using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Spectre.Console;

namespace Bobcat.Console.Specs;

/// <summary>
/// The spec project a <c>bobcat run</c>, <c>preview</c>, <c>pick</c> or <c>watch</c> acts on: found,
/// built, and pointed at the test host executable it produces.
/// </summary>
/// <remarks>
/// <b>Driving the host directly, not <c>dotnet test</c>.</b> An xUnit v3 or TUnit suite on the
/// Microsoft Testing Platform IS an executable, and running it is the one way its exit-time spec
/// rendering reaches a terminal at all — <c>dotnet test</c> keeps a passing test's output to itself.
/// So the tool builds once and then launches the host, which also makes a rerun in watch mode skip
/// MSBuild's evaluation when nothing changed.
/// </remarks>
internal sealed class SpecProject
{
    private SpecProject(string path, string framework, string configuration)
    {
        ProjectPath = path;
        Framework = framework;
        Configuration = configuration;
    }

    public string ProjectPath { get; }
    public string Name => Path.GetFileNameWithoutExtension(ProjectPath);
    public string Directory => Path.GetDirectoryName(ProjectPath)!;
    public string Framework { get; }
    public string Configuration { get; }

    /// <summary>The test host executable, once <see cref="BuildAsync"/> has found it.</summary>
    public string HostPath { get; private set; } = "";

    /// <summary>
    /// The project named — a .csproj, or a directory holding exactly one — or, with nothing named,
    /// the one under the current directory that references Bobcat. Several candidates are offered as
    /// a choice in a terminal and refused anywhere else.
    /// </summary>
    public static async Task<SpecProject> ResolveAsync(string? project, string? framework, string configuration)
    {
        var path = locate(project);
        var frameworks = await frameworksOf(path);

        if (framework is { Length: > 0 })
        {
            if (!frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(path)} targets {string.Join(", ", frameworks)}, not {framework}.");
            }
        }
        else
        {
            // The newest: a spec project that multi-targets is usually proving a range, and the newest
            // is what a developer runs at a desk.
            framework = frameworks.OrderByDescending(versionOf).First();
        }

        return new SpecProject(path, framework, configuration);
    }

    /// <summary>
    /// <c>dotnet build</c>, quietly; its output is shown only when it fails. Then the host path, which
    /// MSBuild reports rather than this guessing at <c>bin/</c> layouts.
    /// </summary>
    public async Task<bool> BuildAsync(bool skipBuild = false)
    {
        if (!skipBuild)
        {
            var (code, output) = await AnsiConsole.Status().StartAsync($"Building {Name} ({Framework})…",
                _ => Shell.RunAsync("dotnet",
                    ["build", ProjectPath, "-f", Framework, "-c", Configuration, "-nologo", "-v", "q", "-clp:NoSummary"]));

            if (code != 0)
            {
                AnsiConsole.MarkupLine($"[red]Build failed for {Markup.Escape(Name)}[/]");
                System.Console.WriteLine(output);
                return false;
            }
        }

        var (_, targetPath) = await Shell.RunAsync("dotnet",
            ["msbuild", ProjectPath, "-getProperty:TargetPath", $"-p:TargetFramework={Framework}", $"-p:Configuration={Configuration}"]);

        var dll = targetPath.Trim();
        var host = OperatingSystem.IsWindows() ? Path.ChangeExtension(dll, ".exe") : Path.ChangeExtension(dll, null);

        if (!File.Exists(host))
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(Name)} built no test host at {Markup.Escape(host)}.[/] A spec project for the "
                + "bobcat tool is an xUnit v3 or TUnit project on the Microsoft Testing Platform, which builds an executable.");
            return false;
        }

        HostPath = host;
        return true;
    }

    /// <summary>The directories whose source a watch reacts to: this project's and every project it references.</summary>
    public IReadOnlyList<string> SourceDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([ProjectPath]);

        while (pending.Count > 0)
        {
            var project = Path.GetFullPath(pending.Pop());
            if (!seen.Add(project) || !File.Exists(project)) continue;

            try
            {
                foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
                {
                    var include = reference.Attribute("Include")?.Value;
                    if (include is null) continue;
                    pending.Push(Path.Combine(Path.GetDirectoryName(project)!, include.Replace('\\', Path.DirectorySeparatorChar)));
                }
            }
            catch
            {
                // An unreadable project file still has a directory worth watching.
            }
        }

        return seen.Select(Path.GetDirectoryName).OfType<string>().Distinct().ToList();
    }

    private static string locate(string? project)
    {
        if (project is { Length: > 0 })
        {
            var full = Path.GetFullPath(project);
            if (File.Exists(full) && full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return full;

            if (System.IO.Directory.Exists(full))
            {
                var here = System.IO.Directory.GetFiles(full, "*.csproj");
                if (here.Length == 1) return here[0];
                return choose(candidatesUnder(full), $"under {full}");
            }

            // `bobcat` with no command runs, so a mistyped command arrives here as a project path
            throw new InvalidOperationException(
                $"There is no project or directory at '{project}', and it is not a bobcat command. "
                + "The commands are run (the default), preview, pick, watch, resident and import-event-model; "
                + "`bobcat help` lists them.");
        }

        var cwd = System.IO.Directory.GetCurrentDirectory();
        var local = System.IO.Directory.GetFiles(cwd, "*.csproj");
        if (local.Length == 1 && referencesBobcat(local[0])) return local[0];

        return choose(candidatesUnder(cwd), "under the current directory");
    }

    private static List<string> candidatesUnder(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return System.IO.Directory.EnumerateFiles(root, "*.csproj", options)
            .Where(x => !x.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "node_modules" or ".git"))
            .Where(referencesBobcat)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A spec project references a Bobcat runner adapter (or Bobcat itself) and is a test project.</summary>
    private static bool referencesBobcat(string csproj)
    {
        try
        {
            var text = File.ReadAllText(csproj);
            return text.Contains("Bobcat", StringComparison.Ordinal)
                   && (text.Contains("xunit", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("TUnit", StringComparison.Ordinal)
                       || text.Contains("Bobcat.Xunit", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static string choose(List<string> candidates, string where)
    {
        if (candidates.Count == 1) return candidates[0];

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Found no spec project {where}: no test project referencing Bobcat. Name one: bobcat run path/to/Specs.csproj");
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new InvalidOperationException(
                $"Found {candidates.Count} spec projects {where}; name one:{Environment.NewLine}"
                + string.Join(Environment.NewLine, candidates.Select(x => "  " + Path.GetRelativePath(System.IO.Directory.GetCurrentDirectory(), x))));
        }

        var cwd = System.IO.Directory.GetCurrentDirectory();
        return AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Which spec project?")
            .PageSize(15)
            .UseConverter(x => Markup.Escape(Path.GetRelativePath(cwd, x)))
            .AddChoices(candidates));
    }

    private static async Task<IReadOnlyList<string>> frameworksOf(string project)
    {
        var (_, output) = await Shell.RunAsync("dotnet",
            ["msbuild", project, "-getProperty:TargetFramework", "-getProperty:TargetFrameworks"]);

        try
        {
            using var json = JsonDocument.Parse(output);
            var properties = json.RootElement.GetProperty("Properties");
            var all = properties.GetProperty("TargetFrameworks").GetString();
            var single = properties.GetProperty("TargetFramework").GetString();

            var frameworks = (string.IsNullOrWhiteSpace(all) ? single ?? "" : all)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (frameworks.Length > 0) return frameworks;
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException($"Could not read the target frameworks of {project}:{Environment.NewLine}{output}");
    }

    private static Version versionOf(string tfm)
        => Version.TryParse(new string(tfm.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()), out var v)
            ? v
            : new Version(0, 0);
}

/// <summary>Run a process to completion and keep what it said.</summary>
internal static class Shell
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string file, IEnumerable<string> arguments,
        IDictionary<string, string?>? environment = null, string? logPath = null, CancellationToken token = default)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (key, value) in environment) start.Environment[key] = value;
        }

        using var process = new Process { StartInfo = start };
        var output = new System.Text.StringBuilder();
        StreamWriter? log = logPath is null ? null : new StreamWriter(logPath, append: false) { AutoFlush = true };
        var gate = new object();

        void line(string? text)
        {
            if (text is null) return;
            lock (gate)
            {
                if (log is null) output.AppendLine(text);
                else log.WriteLine(text);
            }
        }

        process.OutputDataReceived += (_, e) => line(e.Data);
        process.ErrorDataReceived += (_, e) => line(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        finally
        {
            log?.Dispose();
        }

        return (process.ExitCode, output.ToString());
    }
}
