using System.Text.Json;
using System.Xml.Linq;

namespace Bobcat.Monitor.Coordination.GitHub;

/// <summary>
/// The pinned version of one package in one repository's committed package config — the
/// observation a consume node's status derives from. Null Version means the repo does not
/// reference the package (yet): for a consume node that's "the upgrade hasn't happened",
/// which is a normal state, not an error.
/// </summary>
public record PackagePin(
    string Repo,
    string Package,
    string? Version,
    string? Source,
    DateTimeOffset ObservedAt)
{
    public static string KeyFor(string repo, string package) => $"{repo}|{package.ToLowerInvariant()}";
}

/// <summary>Snapshot cache, same shape and same event-store seam as the other observation caches.</summary>
public sealed class PackagePinCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PackagePin> _pins = new();

    public bool Upsert(PackagePin pin)
    {
        var key = PackagePin.KeyFor(pin.Repo, pin.Package);
        lock (_gate)
        {
            var changed = !_pins.TryGetValue(key, out var last)
                          || last.Version != pin.Version
                          || last.Source != pin.Source;
            _pins[key] = pin;
            return changed;
        }
    }

    public PackagePin? Find(string repo, string package)
    {
        lock (_gate) return _pins.GetValueOrDefault(PackagePin.KeyFor(repo, package));
    }
}

/// <summary>
/// The pure wire half of pin observation: fetch a repository's package-config files as blobs
/// in one GraphQL query, then read pinned versions out of the MSBuild XML. Central Package
/// Management's conventional locations only — the repo root and src/ — because that is the
/// JasperFx convention and scanning every csproj through an API is a different tool.
/// </summary>
public static class PackagePins
{
    /// <summary>Candidate config paths, in the order a defined pin wins.</summary>
    public static readonly string[] ConfigPaths = ["Directory.Packages.props", "src/Directory.Packages.props"];

    private static readonly string[] aliases = ["root", "src"];

    public static string BuildQuery(string owner, string name)
    {
        var blobs = string.Join(" ", ConfigPaths.Select((path, i) =>
            $"{aliases[i]}: object(expression: \"HEAD:{path}\") {{ ... on Blob {{ text }} }}"));

        return $"query {{ repository(owner: \"{owner}\", name: \"{name}\") {{ {blobs} }} }}";
    }

    /// <summary>The fetched files as (path, xml) pairs, in pin-precedence order.</summary>
    public static IReadOnlyList<(string Path, string Xml)> ParseResponse(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("repository", out var repository)
            || repository.ValueKind != JsonValueKind.Object)
        {
            var reason = document.RootElement.TryGetProperty("errors", out var errors)
                ? errors.ToString()
                : "no data in response";
            throw new InvalidOperationException($"GitHub gave no repository data for a pin query: {reason}");
        }

        var files = new List<(string, string)>();
        for (var i = 0; i < aliases.Length; i++)
        {
            if (repository.TryGetProperty(aliases[i], out var blob)
                && blob.ValueKind == JsonValueKind.Object
                && blob.TryGetProperty("text", out var text)
                && text.GetString() is { } xml)
            {
                files.Add((ConfigPaths[i], xml));
            }
        }

        return files;
    }

    /// <summary>
    /// The version the files pin for one package — first file (in precedence order) that
    /// defines it wins. Reads <c>PackageVersion</c> (CPM) and <c>PackageReference</c>
    /// entries; a file that will not parse as XML simply defines nothing.
    /// </summary>
    public static (string? Version, string? Source) FindPin(
        IReadOnlyList<(string Path, string Xml)> files, string package)
    {
        foreach (var (path, xml) in files)
        {
            string? version;
            try
            {
                version = XDocument.Parse(xml).Descendants()
                    .Where(x => x.Name.LocalName is "PackageVersion" or "PackageReference")
                    .Where(x => string.Equals((string?)x.Attribute("Include"), package, StringComparison.OrdinalIgnoreCase))
                    .Select(x => (string?)x.Attribute("Version"))
                    .FirstOrDefault(x => x is not null);
            }
            catch
            {
                continue;
            }

            if (version is not null) return (version, path);
        }

        return (null, null);
    }
}
