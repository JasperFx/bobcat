using JasperFx.CommandLine;

namespace Bobcat.Console.Specs;

/// <summary>
/// What <c>bobcat resident</c> is pointed at, resolved to the spec project's test host executable
/// (bobcat#785): the executable itself, its build output folder (<c>bin/Debug/net10.0</c>), the
/// <c>.csproj</c>, or the directory holding that <c>.csproj</c>.
/// </summary>
/// <remarks>
/// A mistake here is an <see cref="InvalidUsageException"/> — one line and exit 1 — because every
/// one of them is something the person typing can fix: there is nothing at the path, the folder
/// holds no executable or several, the project is not built. Nothing here builds anything; a
/// resident runner is relaunched by whatever rebuilt the suite, and a runner that built on its own
/// would run code its parent never saw.
/// </remarks>
internal static class TestHostPath
{
    private const string RuntimeConfig = ".runtimeconfig.json";

    public static async Task<string> ResolveAsync(string given, string? framework, string configuration)
    {
        if (given.Length == 0)
        {
            throw new InvalidUsageException(
                "Name the spec project's test host: its executable, its build output folder, or its .csproj.");
        }

        var full = Path.GetFullPath(given);

        if (File.Exists(full))
        {
            return full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? await fromProjectAsync(full, framework, configuration)
                : full;
        }

        if (Directory.Exists(full))
        {
            var projects = Directory.GetFiles(full, "*.csproj");
            if (projects.Length == 1) return await fromProjectAsync(projects[0], framework, configuration);

            if (projects.Length > 1)
            {
                throw new InvalidUsageException(
                    $"'{full}' holds {projects.Length} projects ({string.Join(", ", projects.Select(Path.GetFileName))}); name the spec project's.");
            }

            return hostIn(full);
        }

        throw new InvalidUsageException(
            $"There is nothing at '{given}'. Name the spec project's test host: its executable, its build output folder, or its .csproj.");
    }

    /// <summary>The host MSBuild says the project builds, as it stands — not built here.</summary>
    private static async Task<string> fromProjectAsync(string csproj, string? framework, string configuration)
    {
        var project = await SpecProject.ResolveAsync(csproj, framework, configuration);
        var (host, exists) = await project.ExpectedHostAsync();

        if (!exists)
        {
            throw new InvalidUsageException(
                $"{project.Name} ({project.Framework}, {project.Configuration}) is not built: there is no test host at '{host}'. "
                + "Build it first, or name the executable or its build output folder; --framework and --configuration choose another build.");
        }

        return host;
    }

    /// <summary>
    /// The one executable in a build output folder: a <c>.runtimeconfig.json</c> names an application
    /// (a library has none), and the apphost beside it is the host. Several is refused by name rather
    /// than guessed at.
    /// </summary>
    private static string hostIn(string directory)
    {
        var hosts = Directory.GetFiles(directory, "*" + RuntimeConfig)
            .Select(x => Path.GetFileName(x)[..^RuntimeConfig.Length])
            .Select(x => Path.Combine(directory, OperatingSystem.IsWindows() ? x + ".exe" : x))
            .Where(File.Exists)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return hosts.Count switch
        {
            1 => hosts[0],
            0 => throw new InvalidUsageException(
                $"'{directory}' holds no test host: no executable with a {RuntimeConfig} beside it. "
                + "Name the spec project's build output folder (bin/Debug/net10.0, once it is built), its .csproj, or the executable itself."),
            _ => throw new InvalidUsageException(
                $"'{directory}' holds {hosts.Count} executables ({string.Join(", ", hosts.Select(Path.GetFileName))}); name the test host itself.")
        };
    }
}
