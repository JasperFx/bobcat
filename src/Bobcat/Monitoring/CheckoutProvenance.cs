namespace Bobcat.Monitoring;

/// <summary>
/// Where an Event Model half was produced: the checkout root, the <c>owner/name</c> of its
/// <c>origin</c> remote and its branch (bobcat#532). Sent as the optional <c>root</c>,
/// <c>repository</c> and <c>branch</c> query parameters on <c>PUT /api/event-model</c> and
/// <c>PUT /api/event-model/{source}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it travels.</b> A console merges every half naming the same model, so halves left over
/// from an older build of the same model kept merging into a newer canvas (stoat#92). The sharp
/// rule — a half from a different repository or checkout than the newest one is stale — needs to
/// know where each half came from, and nothing else on the wire says so.
/// </para>
/// <para>
/// <b>Every field is optional, and so is the whole thing.</b> A console that predates the
/// parameters ignores them, and a field Bobcat cannot read is left off rather than guessed: an
/// absent field is "the rule does not apply to this half", a wrong one is a half called stale that
/// is not. Discovery reads files only, like <see cref="GitInfo"/> — no <c>git</c> process — and
/// never throws, because provenance is decoration on a push that must never fail a run.
/// </para>
/// </remarks>
public sealed record CheckoutProvenance(string Root, string? Repository, string? Branch)
{
    /// <summary>
    /// The provenance of a half produced from <paramref name="directory"/>: the checkout containing
    /// it, or the directory itself when it is in no checkout. Null only when even the directory
    /// cannot be resolved.
    /// </summary>
    public static CheckoutProvenance? Discover(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var checkout = GitInfo.FindCheckout(full);

            return checkout is null
                ? new CheckoutProvenance(full, null, null)
                : new CheckoutProvenance(checkout.Root, checkout.Repository, checkout.Branch);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The query string these fields make, leading <c>?</c> included; empty for none.</summary>
    public string ToQueryString()
    {
        var parts = new List<string> { $"root={Uri.EscapeDataString(Root)}" };
        if (Repository is { Length: > 0 }) parts.Add($"repository={Uri.EscapeDataString(Repository)}");
        if (Branch is { Length: > 0 }) parts.Add($"branch={Uri.EscapeDataString(Branch)}");

        return "?" + string.Join("&", parts);
    }

    /// <summary>
    /// The canonical <c>owner/name</c> a remote URL names, or null when it names no such pair.
    /// Takes the https (<c>https://github.com/owner/name.git</c>), scp-style ssh
    /// (<c>git@github.com:owner/name.git</c>) and <c>ssh://</c> spellings.
    /// </summary>
    public static string? RepositoryFromRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;

        var url = remote.Trim();
        string path;

        if (url.Contains("://"))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            path = uri.AbsolutePath;
        }
        else if (url.IndexOf(':') is var colon and > 0)
        {
            // scp-style: git@github.com:owner/name.git
            path = url[(colon + 1)..];
        }
        else
        {
            // A local path for a remote is not a repository anybody else can name.
            return null;
        }

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        var owner = segments[^2];
        var name = segments[^1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        return owner.Length == 0 || name.Length == 0 ? null : $"{owner}/{name}";
    }
}
