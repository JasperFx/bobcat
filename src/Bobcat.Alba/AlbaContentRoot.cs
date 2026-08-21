using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Bobcat.Runtime;

/// <summary>
/// Resolves the content root for a host bootstrapped through <c>WebApplicationFactory</c>, the
/// way WebApplicationFactory itself would — but without the two habits that made it fail for any
/// web project under <c>src/</c> (issue #62 gap 9): it reads <c>MvcTestingAppManifest.json</c>
/// from the test's output directory rather than from the <em>current working directory</em>, and
/// its solution-relative fallback checks that the directory exists, searches for the project file
/// when it does not, and lands on the test output directory (where the build has already copied
/// the host's content files) rather than on a path that is not there.
/// </summary>
/// <remarks>
/// WebApplicationFactory's own order is: <c>TEST_CONTENTROOT_&lt;ASSEMBLY&gt;</c> setting, then the
/// manifest if <c>File.Exists("MvcTestingAppManifest.json")</c> — relative to the working
/// directory, which is the test output only when a runner chooses to make it so (xUnit does;
/// a Bobcat MTP host run from the repo root does not) — then
/// <c>[WebApplicationFactoryContentRoot]</c>, then <c>&lt;solution dir&gt;/&lt;assembly name&gt;</c>
/// unchecked. That last guess is wrong for <c>src/&lt;Project&gt;</c>, <c>samples/&lt;Project&gt;</c> and
/// every nested layout, and surfaces as a bare <see cref="DirectoryNotFoundException"/> when the
/// host builds. This resolver mirrors the order and fixes the two habits.
/// </remarks>
public static class AlbaContentRoot
{
    public const string ManifestFileName = "MvcTestingAppManifest.json";

    /// <summary>
    /// Directory names never descended into while searching for a project file.
    /// </summary>
    private static readonly HashSet<string> skippedDirectories =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "packages", "TestResults" };

    /// <summary>How deep below the solution directory the project-file search looks.</summary>
    private const int searchDepth = 5;

    /// <summary>
    /// The outcome of a resolution: <see cref="Path"/> is the content root to use, or null when
    /// the decision is deliberately left to WebApplicationFactory (a <c>TEST_CONTENTROOT_*</c>
    /// setting is present). <see cref="Source"/> says how it was found, for diagnostics.
    /// </summary>
    public sealed record Resolution(string? Path, string Source)
    {
        public override string ToString() => Path == null ? Source : $"{Path} ({Source})";
    }

    /// <summary>
    /// Resolve the content root for the host whose entry point lives in
    /// <paramref name="entryPointAssembly"/>, from the current process: <see cref="AppContext.BaseDirectory"/>
    /// as the test output directory, and every loaded assembly that references the entry-point
    /// assembly as a candidate carrier of <c>[WebApplicationFactoryContentRoot]</c>.
    /// </summary>
    public static Resolution Resolve(Assembly entryPointAssembly)
    {
        var name = entryPointAssembly.GetName();
        var simpleName = name.Name ?? string.Empty;
        var testAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a != entryPointAssembly)
            .Where(a => a.GetReferencedAssemblies().Any(r => string.Equals(r.Name, simpleName, StringComparison.Ordinal)))
            .ToArray();

        return Resolve(name, AppContext.BaseDirectory, testAssemblies);
    }

    /// <summary>
    /// The pure form: resolve for an entry-point assembly identity, given the test output
    /// directory and the assemblies that may carry <c>[WebApplicationFactoryContentRoot]</c>.
    /// </summary>
    public static Resolution Resolve(AssemblyName entryPoint, string baseDirectory, IEnumerable<Assembly>? testAssemblies = null)
    {
        var simpleName = entryPoint.Name ?? string.Empty;
        var fullName = entryPoint.FullName;

        // 1. An explicit setting: WebApplicationFactory honours TEST_CONTENTROOT_<ASSEMBLY> from the
        //    host's configuration (DOTNET_/ASPNETCORE_-prefixed environment variables reach it).
        //    When one is present the caller meant it, so leave the decision to the factory.
        var settingSuffix = simpleName.ToUpperInvariant().Replace(".", "_");
        foreach (var prefix in new[] { "", "ASPNETCORE_", "DOTNET_" })
        {
            var setting = $"{prefix}TEST_CONTENTROOT_{settingSuffix}";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(setting)))
                return new Resolution(null, $"left to WebApplicationFactory: environment variable {setting} is set");
        }

        // 2. The manifest Microsoft.AspNetCore.Mvc.Testing writes into the test output for every
        //    referenced project — the authoritative answer, read from where it actually is.
        var manifest = Path.Combine(baseDirectory, ManifestFileName);
        if (File.Exists(manifest))
        {
            var fromManifest = readManifest(manifest, fullName, simpleName, baseDirectory);
            if (fromManifest != null && Directory.Exists(fromManifest))
                return new Resolution(fromManifest, $"{ManifestFileName} in the test output directory");
        }

        // 3. [assembly: WebApplicationFactoryContentRoot] on a test assembly, same rules as the
        //    factory: relative to the test output, confirmed by its marker file, lowest priority first.
        foreach (var attribute in contentRootAttributes(testAssemblies, fullName, simpleName))
        {
            var candidate = Path.GetFullPath(Path.Combine(baseDirectory, attribute.ContentRootPath));
            var marker = Path.Combine(candidate, Path.GetFileName(attribute.ContentRootTest));
            if (File.Exists(marker))
                return new Resolution(candidate, "[WebApplicationFactoryContentRoot] on the test assembly");
        }

        // 4. Solution-relative, but checked: <solution>/<assembly name> if it exists, otherwise the
        //    directory holding <assembly name>.csproj anywhere reasonable below the solution — which
        //    is what src/<Project>, samples/<Project> and the nested Tests/ layouts all need.
        var solutionDirectory = FindSolutionDirectory(baseDirectory);
        if (solutionDirectory != null)
        {
            var sibling = Path.Combine(solutionDirectory, simpleName);
            if (Directory.Exists(sibling))
                return new Resolution(sibling, "<solution directory>/<assembly name>");

            var projectDirectory = findProjectDirectory(solutionDirectory, simpleName + ".csproj", searchDepth);
            if (projectDirectory != null)
                return new Resolution(projectDirectory, $"directory of {simpleName}.csproj below the solution directory");
        }

        // 5. The test output directory always exists, and the build copies the referenced host's
        //    content items (appsettings*.json, wwwroot) into it — so a host with nothing else to
        //    serve starts, instead of failing on a directory that was never there.
        return new Resolution(baseDirectory, "test output directory (no manifest entry, attribute, or project directory found)");
    }

    private static string? readManifest(string manifest, string fullName, string simpleName, string baseDirectory)
    {
        Dictionary<string, string>? data;
        try
        {
            data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(manifest));
        }
        catch (JsonException)
        {
            return null;
        }

        if (data == null) return null;

        // The manifest is keyed by the project's full assembly name; match on the simple name as
        // well so a version bump between build and run is not a reason to miss.
        var contentRoot = data.TryGetValue(fullName, out var exact) ? exact
            : data.FirstOrDefault(kv => string.Equals(new AssemblyName(kv.Key).Name, simpleName, StringComparison.OrdinalIgnoreCase)).Value;

        if (string.IsNullOrEmpty(contentRoot)) return null;
        return contentRoot == "~" ? baseDirectory : contentRoot;
    }

    private static IEnumerable<WebApplicationFactoryContentRootAttribute> contentRootAttributes(
        IEnumerable<Assembly>? assemblies, string fullName, string simpleName)
    {
        if (assemblies == null) return [];

        return assemblies
            .SelectMany(a =>
            {
                try { return a.GetCustomAttributes<WebApplicationFactoryContentRootAttribute>(); }
                catch { return []; }
            })
            .Where(a => string.Equals(a.Key, fullName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a.Key, simpleName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Priority)
            .ToArray();
    }

    /// <summary>
    /// Walk up from <paramref name="start"/> to the first directory holding a <c>.sln</c> or
    /// <c>.slnx</c> file.
    /// </summary>
    public static string? FindSolutionDirectory(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory != null)
        {
            if (directory.Exists && directory.EnumerateFiles().Any(f =>
                    f.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                    || f.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? findProjectDirectory(string root, string projectFileName, int depth)
    {
        if (depth < 0) return null;

        IEnumerable<string> entries;
        try
        {
            if (File.Exists(Path.Combine(root, projectFileName))) return root;
            entries = Directory.EnumerateDirectories(root);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        foreach (var child in entries)
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || skippedDirectories.Contains(name)) continue;

            var found = findProjectDirectory(child, projectFileName, depth - 1);
            if (found != null) return found;
        }

        return null;
    }
}
