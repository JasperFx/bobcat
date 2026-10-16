using System.Collections.Concurrent;

namespace Bobcat.Ui.Repositories;

/// <summary>
/// One canonical name for a repository (stoat#64): <c>owner/name</c>, the shape plan nodes, issue
/// refs and the repository records already use. Plans, agent sessions and test runs used to answer
/// "which repository is this?" three different ways (<c>JasperFx/bobcat</c>, <c>bobcat</c>, a local
/// path) and every screen that joins them needs the same answer. This is where a local path becomes
/// that answer.
/// </summary>
public interface IRepositoryIdentity
{
    /// <summary>
    /// The <c>owner/name</c> of the repository a local path belongs to, read from that checkout's
    /// git remote — or null when the path is not in a git checkout, has no usable remote, or is not
    /// on this machine. Null is an answer ("unattributed"), never a guess.
    /// </summary>
    string? SlugFor(string? path);
}

/// <summary>
/// Resolves a path to <c>owner/name</c> from the checkout's own <c>origin</c> remote, by reading the
/// git files directly (no <c>git</c> process per sweep). OBSERVED, in the same sense the session
/// files are: what the checkout on this box says about itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the remote and not the folder name.</b> The folder name is what the roster used to go on,
/// and it is wrong exactly where it matters: a worktree under <c>~/code/_bobcat_worktrees/pkg-table</c>
/// read as a repository called <c>pkg-table</c>, and a clone checked out under any other name read as
/// that name. A worktree shares its parent's remote, so it attributes to the right repository by
/// construction.
/// </para>
/// <para>
/// Cached per checkout root for <see cref="CacheFor"/>: the session sweep asks every few seconds, a
/// remote almost never changes, and a renamed one is picked up within minutes.
/// </para>
/// </remarks>
public sealed class GitRepositoryIdentity : IRepositoryIdentity
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (string? Slug, DateTimeOffset At)> _cache = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public GitRepositoryIdentity(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public string? SlugFor(string? path)
    {
        // Only a rooted path names a place. A relative one — a run that never sent run_started reports
        // "(unknown)" — would be resolved against THIS process's working directory and attribute the
        // run to whatever checkout the host happens to be running in.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;

        var root = GitRemotes.FindRoot(path);
        if (root is null) return null;

        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(root, out var known) && now - known.At < CacheFor) return known.Slug;

        var slug = GitRemotes.SlugForRoot(root);
        _cache[root] = (slug, now);
        return slug;
    }
}

/// <summary>The pure half: finding a checkout's git files and reading its remote.</summary>
public static class GitRemotes
{
    /// <summary>The checkout root above a path: the nearest directory holding a <c>.git</c> entry,
    /// which is a directory in a clone and a FILE in a worktree.</summary>
    public static string? FindRoot(string path)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(path);
        }
        catch
        {
            return null;
        }

        for (var current = directory; current is not null; current = current.Parent)
        {
            var git = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return current.FullName;
        }

        return null;
    }

    /// <summary>
    /// The <c>config</c> file a checkout reads its remotes from. A clone's is <c>.git/config</c>. A
    /// worktree's <c>.git</c> file says <c>gitdir: …/.git/worktrees/x</c>, and that directory's
    /// <c>commondir</c> leads back to the main <c>.git</c>, where the remotes live.
    /// </summary>
    public static string? ConfigFor(string root)
    {
        var git = Path.Combine(root, ".git");
        if (Directory.Exists(git)) return existing(Path.Combine(git, "config"));
        if (!File.Exists(git)) return null;

        string? gitDir = null;
        try
        {
            foreach (var line in File.ReadLines(git))
            {
                if (line.StartsWith("gitdir:", StringComparison.Ordinal)) gitDir = line["gitdir:".Length..].Trim();
            }
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrEmpty(gitDir)) return null;
        if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(root, gitDir));

        var commonDirFile = Path.Combine(gitDir, "commondir");
        var common = gitDir;
        try
        {
            if (File.Exists(commonDirFile))
            {
                var relative = File.ReadAllText(commonDirFile).Trim();
                common = Path.IsPathRooted(relative) ? relative : Path.GetFullPath(Path.Combine(gitDir, relative));
            }
        }
        catch
        {
            return null;
        }

        return existing(Path.Combine(common, "config"));
    }

    /// <summary>The <c>owner/name</c> a checkout's remote names, or null.</summary>
    public static string? SlugForRoot(string root)
    {
        var config = ConfigFor(root);
        if (config is null) return null;

        try
        {
            return RemoteUrl(File.ReadAllText(config)) is { } url ? SlugOf(url) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The remote a checkout is known by: <c>origin</c> when there is one, otherwise the only remote
    /// there is. Several remotes and no origin is ambiguous, and ambiguity refuses to guess.
    /// </summary>
    public static string? RemoteUrl(string config)
    {
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        string? section = null;

        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = remoteName(line);
                continue;
            }

            if (section is null) continue;
            var equals = line.IndexOf('=');
            if (equals < 0 || line[..equals].Trim() != "url") continue;
            urls.TryAdd(section, line[(equals + 1)..].Trim());
        }

        if (urls.TryGetValue("origin", out var origin)) return origin;
        return urls.Count == 1 ? urls.Values.Single() : null;
    }

    /// <summary>
    /// <c>owner/name</c> from any of the URL shapes git accepts:
    /// <c>https://github.com/JasperFx/bobcat.git</c>, <c>git@github.com:JasperFx/bobcat.git</c>,
    /// <c>ssh://git@github.com/JasperFx/bobcat</c>. Null for a local-path remote, which names no
    /// repository anyone else would recognise.
    /// </summary>
    public static string? SlugOf(string url)
    {
        var text = url.Trim();
        if (text.Length == 0) return null;

        string path;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.IsFile) return null;
            path = uri.AbsolutePath;
        }
        else
        {
            // scp-like: [user@]host:owner/name — but a Windows drive path is not one.
            var colon = text.IndexOf(':');
            if (colon <= 1 || text.StartsWith('/') || text.StartsWith('.')) return null;
            path = text[(colon + 1)..];
        }

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        var owner = segments[^2];
        var name = segments[^1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return owner.Length > 0 && name.Length > 0 ? $"{owner}/{name}" : null;
    }

    private static string? remoteName(string header)
    {
        // [remote "origin"]
        const string prefix = "[remote \"";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = header.IndexOf('"', prefix.Length);
        return end > prefix.Length ? header[prefix.Length..end] : null;
    }

    private static string? existing(string path) => File.Exists(path) ? path : null;
}
