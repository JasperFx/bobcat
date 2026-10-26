using System.Text.Json;
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
/// holds no executable or several it cannot choose between, the project is not built. Nothing here builds anything; a
/// resident runner is relaunched by whatever rebuilt the suite, and a runner that built on its own
/// would run code its parent never saw.
/// </remarks>
internal static class TestHostPath
{
    private const string RuntimeConfig = ".runtimeconfig.json";
    private const string DepsFile = ".deps.json";

    /// <summary>
    /// A library in an executable's <c>.deps.json</c> that makes it a test host rather than the
    /// application under test: the testing platform, a framework that runs on it, or Bobcat.
    /// Matched on the library's name, before its <c>/version</c>.
    /// </summary>
    private static readonly string[] testHostLibraryPrefixes =
        ["Microsoft.Testing.Platform", "xunit.v3", "TUnit", "NUnit", "MSTest", "Bobcat"];

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

            return await hostInAsync(full, framework, configuration);
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
    /// The test host in a build output folder. A <c>.runtimeconfig.json</c> names an application (a
    /// library has none), and the apphost beside it is an executable. One is the host. Several is the
    /// usual spec project that references the application it tests (bobcat#797), so they are
    /// narrowed in order: the one the folder's own project builds, then the only one that is a test
    /// host. Still several is refused by name rather than guessed at.
    /// </summary>
    private static async Task<string> hostInAsync(string directory, string? framework, string configuration)
    {
        var hosts = Directory.GetFiles(directory, "*" + RuntimeConfig)
            .Select(x => Path.GetFileName(x)[..^RuntimeConfig.Length])
            .Where(x => File.Exists(Path.Combine(directory, executableName(x))))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (hosts.Count == 0)
        {
            throw new InvalidUsageException(
                $"'{directory}' holds no test host: no executable with a {RuntimeConfig} beside it. "
                + "Name the spec project's build output folder (bin/Debug/net10.0, once it is built), its .csproj, or the executable itself.");
        }

        if (hosts.Count == 1) return Path.Combine(directory, executableName(hosts[0]));

        // 1. The folder's own project's assembly. A spec project's bin also holds every executable it
        //    references, but only one of them is the project's own build.
        if (await owningAssemblyAsync(directory, framework, configuration) is { } owner
            && hosts.FirstOrDefault(x => string.Equals(x, owner, StringComparison.OrdinalIgnoreCase)) is { } owned)
        {
            return Path.Combine(directory, executableName(owned));
        }

        // 2. The only one that carries a test framework: an application under test references none.
        var testHosts = hosts.Where(x => isTestHost(directory, x)).ToList();
        if (testHosts.Count == 1) return Path.Combine(directory, executableName(testHosts[0]));

        throw new InvalidUsageException(
            $"'{directory}' holds {hosts.Count} executables ({string.Join(", ", hosts)}); name the test host itself.");
    }

    private static string executableName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>
    /// The assembly name of the project whose build output <paramref name="directory"/> is — the
    /// nearest directory above it holding exactly one <c>.csproj</c>, read through MSBuild like a
    /// suite's name is, so an <c>AssemblyName</c> that differs from the file name is honoured. The
    /// usual <c>bin/&lt;configuration&gt;/&lt;tfm&gt;</c> layout says which build to evaluate; the
    /// flags stand in for anything else. Null when there is no such project, or it cannot be read,
    /// which leaves the choice to the next rule rather than failing it.
    /// </summary>
    private static async Task<string?> owningAssemblyAsync(string directory, string? framework, string configuration)
    {
        var current = Directory.GetParent(directory);
        while (current is not null)
        {
            var projects = Directory.GetFiles(current.FullName, "*.csproj");
            if (projects.Length > 1) return null;
            if (projects.Length == 1) break;
            current = current.Parent;
        }

        if (current is null) return null;

        var csproj = Directory.GetFiles(current.FullName, "*.csproj")[0];
        var segments = Path.GetRelativePath(current.FullName, directory).Split(Path.DirectorySeparatorChar);
        if (segments is ["bin", var builtConfiguration, var builtFramework, ..])
        {
            configuration = builtConfiguration;
            framework ??= builtFramework;
        }

        try
        {
            SpecProject project;
            try
            {
                project = await SpecProject.ResolveAsync(csproj, framework, configuration);
            }
            catch (InvalidUsageException)
            {
                // The folder name is not one of the project's frameworks: evaluate its default.
                project = await SpecProject.ResolveAsync(csproj, null, configuration);
            }

            return await project.SuiteNameAsync();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the executable's <c>.deps.json</c> lists a test framework or Bobcat among its
    /// libraries. Its own entry is skipped: every deps file lists the application itself, and an
    /// application named <c>Bobcat.Something</c> is not a test host for that.
    /// </summary>
    private static bool isTestHost(string directory, string host)
    {
        var depsFile = Path.Combine(directory, host + DepsFile);
        if (!File.Exists(depsFile)) return false;

        try
        {
            using var stream = File.OpenRead(depsFile);
            using var deps = JsonDocument.Parse(stream);
            if (!deps.RootElement.TryGetProperty("libraries", out var libraries)
                || libraries.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return libraries.EnumerateObject()
                .Select(x => x.Name.Split('/')[0])
                .Where(name => !name.Equals(host, StringComparison.OrdinalIgnoreCase))
                .Any(name => testHostLibraryPrefixes.Any(prefix =>
                    name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
