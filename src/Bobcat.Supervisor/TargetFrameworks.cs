using System.Text.RegularExpressions;

namespace Bobcat.Supervisor;

/// <summary>
/// Which of a project's target frameworks a gate runs (bobcat#729). The default is the newest
/// only; <see cref="All"/> is the <c>--framework all</c> opt-in, and <see cref="Only"/> pins a
/// list.
/// </summary>
/// <remarks>
/// Newest-only halves a gate for a project that multi-targets, and CI already runs the matrix.
/// A pinned framework a project does not target falls back to that project's newest — the Alba
/// shape, where one project is net10.0-only while every workflow passes <c>--framework net9.0</c>
/// — and the plan says so.
/// </remarks>
public sealed record FrameworkSelection
{
    private FrameworkSelection(string kind, IReadOnlyList<string> frameworks)
    {
        Kind = kind;
        Frameworks = frameworks;
    }

    /// <summary>The newest framework each project declares. The default.</summary>
    public static FrameworkSelection Newest { get; } = new("newest", []);

    /// <summary>Every framework each project declares.</summary>
    public static FrameworkSelection All { get; } = new("all", []);

    /// <summary>These frameworks, each project falling back to its newest when it does not target one.</summary>
    public static FrameworkSelection Only(params string[] frameworks)
    {
        if (frameworks.Length == 0) throw new ArgumentException("Name at least one framework.", nameof(frameworks));
        return new FrameworkSelection("only", frameworks.Select(f => f.Trim()).ToArray());
    }

    /// <summary>
    /// Parse a command-line spelling: null or empty is <see cref="Newest"/>, <c>all</c> is
    /// <see cref="All"/>, anything else is a <c>;</c>- or <c>,</c>-separated <see cref="Only"/>.
    /// </summary>
    public static FrameworkSelection Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Newest;
        if (text.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return All;
        if (text.Trim().Equals("newest", StringComparison.OrdinalIgnoreCase)) return Newest;

        return Only(text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    internal string Kind { get; }

    /// <summary>The pinned frameworks for <see cref="Only"/>; empty otherwise.</summary>
    public IReadOnlyList<string> Frameworks { get; }

    /// <summary>
    /// The frameworks to run for a project declaring <paramref name="declared"/>, in the order
    /// they should be reported. Never empty for a non-empty declaration.
    /// </summary>
    public IReadOnlyList<string> Select(IReadOnlyList<string> declared)
    {
        if (declared.Count == 0) return [];

        var newest = TargetFrameworks.Newest(declared);

        return Kind switch
        {
            "all" => declared.OrderByDescending(f => f, TargetFrameworks.Comparer).ToArray(),
            "only" => Frameworks
                .Select(wanted => declared.FirstOrDefault(d => d.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ?? newest)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _ => [newest]
        };
    }

    public override string ToString() => Kind == "only" ? string.Join(";", Frameworks) : Kind;
}

/// <summary>
/// Target framework monikers as the gate reads and orders them: <c>net10.0</c> is newer than
/// <c>net9.0</c>, which an ordinal sort gets wrong.
/// </summary>
public static partial class TargetFrameworks
{
    /// <summary>Newest first by version, then ordinal, so the order is total and deterministic.</summary>
    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(compare);

    /// <summary>The newest moniker of the list.</summary>
    public static string Newest(IEnumerable<string> frameworks)
        => frameworks.OrderByDescending(f => f, Comparer).First();

    /// <summary>
    /// The frameworks a project file declares — <c>TargetFramework</c> or <c>TargetFrameworks</c>,
    /// read textually. Empty when the file declares them through a property the gate cannot
    /// evaluate; declare them on the project then (<see cref="GateProject.Targeting"/>).
    /// </summary>
    public static IReadOnlyList<string> Declared(string projectPath)
    {
        if (!File.Exists(projectPath)) return [];

        var match = targetFrameworks().Match(File.ReadAllText(projectPath));
        if (!match.Success) return [];

        return match.Groups[1].Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => !f.Contains("$("))
            .ToArray();
    }

    /// <summary>The version a moniker names — <c>net10.0-windows</c> is 10.0 — or null for one that names none.</summary>
    public static Version? VersionOf(string framework)
    {
        var match = version().Match(framework);
        if (!match.Success) return null;

        var digits = match.Groups[1].Value;
        if (digits.Contains('.')) return Version.TryParse(digits, out var v) ? v : null;

        // net48, net472: the old spelling without dots.
        return digits.Length switch
        {
            1 => new Version(int.Parse(digits), 0),
            2 => new Version(int.Parse(digits[..1]), int.Parse(digits[1..])),
            _ => new Version(int.Parse(digits[..1]), int.Parse(digits[1..2]), int.Parse(digits[2..]))
        };
    }

    private static int compare(string? x, string? y)
    {
        var vx = x is null ? null : VersionOf(x);
        var vy = y is null ? null : VersionOf(y);

        if (vx is not null && vy is not null && vx != vy) return vx.CompareTo(vy);
        if (vx is null != vy is null) return vx is null ? -1 : 1;

        return string.CompareOrdinal(x, y);
    }

    [GeneratedRegex(@"<TargetFrameworks?>([^<]+)</TargetFrameworks?>")]
    private static partial Regex targetFrameworks();

    [GeneratedRegex(@"^net(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex version();
}
