using System.Diagnostics;

namespace Bobcat.Console.Tests;

/// <summary>The built <c>bobcat</c> tool, driven as a person drives it: a process, its exit code and its two streams.</summary>
internal static class Tool
{
    /// <summary>The configuration this test host was built in, which is the one the tool was built in too.</summary>
    public static string Configuration { get; } = Path.GetFileName(
        Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

    /// <summary>The repository's <c>src/</c>, found from this test host's own output directory.</summary>
    public static string SourceDirectory { get; } = locateSource();

    /// <summary>The <c>bobcat</c> executable. Its assembly is <c>Bobcat.Cli</c> (see Bobcat.Console.csproj).</summary>
    public static string Executable { get; } = Path.Combine(
        BinDirectoryOf("Bobcat.Console"), OperatingSystem.IsWindows() ? "Bobcat.Cli.exe" : "Bobcat.Cli");

    /// <summary>A project's build output folder for this configuration, <c>src/&lt;project&gt;/bin/&lt;configuration&gt;/net10.0</c>.</summary>
    public static string BinDirectoryOf(string project)
        => Path.Combine(SourceDirectory, project, "bin", Configuration, "net10.0");

    private static string locateSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the src directory.");
    }

    public static async Task<(int ExitCode, string Output, string Error)> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath()
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        // Never reach for a console on the box
        start.Environment["BOBCAT_MONITOR"] = "0";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await output, await error);
    }

    public static string[] Lines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
