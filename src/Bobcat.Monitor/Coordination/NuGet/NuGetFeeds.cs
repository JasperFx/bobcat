using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Bobcat.Monitor.Coordination.NuGet;

/// <summary>One feed's version listing. Empty means "package not published here (yet)" — a
/// brand-new package's first publish is the normal case, not an error.</summary>
public interface INuGetFeed
{
    Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken ct);
}

/// <summary>
/// Resolves feed NAMES to feeds. Plans reference feeds by name only; URLs, paths, and
/// credentials live here, in monitor configuration — a plan document never carries a secret
/// (docs/agent-coordination-design.md). Shapes under <c>Monitor:Feeds:{name}:</c>
///
///  - <c>Url</c> — a V3 service index (https://.../index.json). Optional <c>Username</c> +
///    <c>Password</c> become basic auth (GitHub Packages style: any username + a PAT);
///    <c>Password</c> alone becomes a bearer token.
///  - <c>Path</c> — a local folder of .nupkg files, the "Publish Nuget Locally" step.
///
/// "nuget.org" resolves to the public service index unless configuration overrides it.
/// Unknown names resolve to null — the poller reports that as a wiring fault on the node,
/// because a plan naming a feed nobody configured must surface, not idle as unknown.
/// </summary>
public sealed class NuGetFeeds
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _http;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, INuGetFeed?> _resolved = new();

    public const string DefaultFeedName = "nuget.org";
    public const string DefaultServiceIndex = "https://api.nuget.org/v3/index.json";

    public NuGetFeeds(IConfiguration configuration, IHttpClientFactory http)
    {
        _configuration = configuration;
        _http = http;
    }

    public INuGetFeed? Resolve(string name)
    {
        lock (_gate)
        {
            if (_resolved.TryGetValue(name, out var known)) return known;

            var feed = build(name);
            _resolved[name] = feed;
            return feed;
        }
    }

    private INuGetFeed? build(string name)
    {
        var section = _configuration.GetSection($"Monitor:Feeds:{name}");

        if (section["Path"] is { } path) return new FolderFeed(path);

        var url = section["Url"] ?? (name == DefaultFeedName ? DefaultServiceIndex : null);
        if (url is null) return null;

        return new FlatContainerFeed(_http.CreateClient("nuget"), url, section["Username"], section["Password"]);
    }
}

/// <summary>A directory of .nupkg files — the local-publish step's feed.</summary>
public sealed class FolderFeed : INuGetFeed
{
    private readonly string _path;

    public FolderFeed(string path) => _path = path;

    public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken ct)
    {
        if (!Directory.Exists(_path)) return Task.FromResult<IReadOnlyList<string>>([]);

        var prefix = packageId + ".";
        var versions = new List<string>();

        foreach (var file in Directory.EnumerateFiles(_path, "*.nupkg"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.EndsWith(".symbols", StringComparison.OrdinalIgnoreCase)) continue;
            if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            // The remainder must BE a version — otherwise this is a longer package id that
            // happens to share the prefix (Foo.Bar.1.0.0 vs Foo.Bar.Baz.1.0.0).
            var rest = stem[prefix.Length..];
            if (PackageVersion.TryParse(rest) is not null) versions.Add(rest);
        }

        return Task.FromResult<IReadOnlyList<string>>(versions);
    }
}

/// <summary>
/// The V3 protocol feed: resolves PackageBaseAddress from the service index once, then reads
/// <c>{base}/{id-lower}/index.json</c>. A 404 at the package level is "not published yet";
/// only the service index failing is an actual fault.
/// </summary>
public sealed class FlatContainerFeed : INuGetFeed
{
    private readonly HttpClient _http;
    private readonly string _serviceIndexUrl;
    private Task<string>? _baseAddress;

    public FlatContainerFeed(HttpClient http, string serviceIndexUrl, string? username, string? password)
    {
        _http = http;
        _serviceIndexUrl = serviceIndexUrl;

        if (password is not null)
        {
            _http.DefaultRequestHeaders.Authorization = username is not null
                ? new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")))
                : new AuthenticationHeaderValue("Bearer", password);
        }
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken ct)
    {
        _baseAddress ??= resolveBaseAddress(ct);
        var baseAddress = await _baseAddress;

        using var response = await _http.GetAsync(
            $"{baseAddress}{packageId.ToLowerInvariant()}/index.json", ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("versions", out var versions)) return [];

        return versions.EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();
    }

    private async Task<string> resolveBaseAddress(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(_serviceIndexUrl, ct);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var resource in document.RootElement.GetProperty("resources").EnumerateArray())
            {
                if (resource.TryGetProperty("@type", out var type)
                    && type.GetString()?.StartsWith("PackageBaseAddress/3.0.0") == true
                    && resource.TryGetProperty("@id", out var id)
                    && id.GetString() is { } address)
                {
                    return address.EndsWith('/') ? address : address + "/";
                }
            }

            throw new InvalidOperationException(
                $"service index {_serviceIndexUrl} exposes no PackageBaseAddress resource");
        }
        catch
        {
            // A failed resolution must not poison every later sweep with the cached failure.
            _baseAddress = null;
            throw;
        }
    }
}
