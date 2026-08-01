namespace Bobcat.Monitor.Coordination.NuGet;

/// <summary>
/// Just enough NuGet version semantics for the feed watcher: up to four numeric parts plus a
/// prerelease tag, ordered SemVer-style (a prerelease sorts below its release). Deliberately
/// not a full SemVer 2 implementation — the watcher compares versions and classifies bump
/// tiers, it doesn't validate anyone's versioning scheme.
/// </summary>
public sealed record PackageVersion(
    string Text, int Major, int Minor, int Patch, int Revision, string? Prerelease)
    : IComparable<PackageVersion>
{
    public static PackageVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim();

        // Build metadata never participates in ordering.
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];

        string? prerelease = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (prerelease.Length == 0) return null;
        }

        var parts = value.Split('.');
        if (parts.Length is < 1 or > 4) return null;

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0) return null;
        }

        return new PackageVersion(text.Trim(), numbers[0], numbers[1], numbers[2], numbers[3], prerelease);
    }

    public int CompareTo(PackageVersion? other)
    {
        if (other is null) return 1;

        var byNumbers = Major.CompareTo(other.Major);
        if (byNumbers == 0) byNumbers = Minor.CompareTo(other.Minor);
        if (byNumbers == 0) byNumbers = Patch.CompareTo(other.Patch);
        if (byNumbers == 0) byNumbers = Revision.CompareTo(other.Revision);
        if (byNumbers != 0) return byNumbers;

        // Same numbers: a release outranks any prerelease; two prereleases compare ordinally.
        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (mine, theirs) => string.CompareOrdinal(mine, theirs)
        };
    }

    /// <summary>
    /// Does moving from <paramref name="baseline"/> to this version match the declared bump
    /// tier? Strict on purpose: a major release does NOT satisfy a declared fix bump — the
    /// plan said what was supposed to happen, and anything else is a mismatch to report,
    /// never silently reconciled (docs/agent-coordination-design.md).
    /// </summary>
    public bool SatisfiesBumpFrom(PackageVersion baseline, BumpKind bump) => bump switch
    {
        BumpKind.Fix => Major == baseline.Major && Minor == baseline.Minor && CompareTo(baseline) > 0,
        BumpKind.Minor => Major == baseline.Major && Minor > baseline.Minor,
        BumpKind.Major => Major > baseline.Major,
        _ => false
    };

    public override string ToString() => Text;
}
