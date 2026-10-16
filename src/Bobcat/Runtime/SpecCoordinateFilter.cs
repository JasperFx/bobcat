namespace Bobcat.Runtime;

/// <summary>
/// Selecting specifications by where they sit on the Event Model, and by trait (bobcat#573):
/// <c>bobcat run --slice X --chapter Y --domain Z --pattern View --trait K=V</c>, resolved through a
/// suite's <see cref="SpecManifest"/> into identities.
/// </summary>
/// <remarks>
/// <para>
/// <b>A repeated flag ORs, different flags AND.</b> <c>--slice A --slice B --pattern View</c> is
/// "slice A or B, and a View". There is deliberately no expression language: nothing has needed NOT.
/// </para>
/// <para>
/// <b>Refuse rather than guess.</b> Values match whole and ignoring case — never as a substring, so
/// <c>--slice Deposit</c> does not quietly also run "Deposit Refund". A value no specification has is
/// refused <em>by name</em>, with the values that exist, rather than narrowing the run to nothing: a
/// filter matching nothing runs nothing and reads as green. A combination that every value exists for
/// but no specification satisfies is refused the same way.
/// </para>
/// <para>
/// <b><c>--trait</c> sees the coordinates too</b>, under the names the adapters project them as
/// (<c>Slice</c>, <c>Chapter</c>, <c>Domain</c>, <c>Pattern</c>), so <c>--trait Slice=X</c> means
/// what <c>dotnet test --filter-trait "Slice=X"</c> means.
/// </para>
/// </remarks>
public sealed class SpecCoordinateFilter
{
    public IReadOnlyList<string> Slices { get; init; } = [];

    public IReadOnlyList<string> Chapters { get; init; } = [];

    public IReadOnlyList<string> Domains { get; init; } = [];

    public IReadOnlyList<string> Patterns { get; init; } = [];

    /// <summary>Each <c>--trait</c> as written, <c>Name=Value</c>.</summary>
    public IReadOnlyList<string> Traits { get; init; } = [];

    /// <summary>Whether any flag was given — when none was, the filter selects everything.</summary>
    public bool NarrowsAnything
        => Slices.Count > 0 || Chapters.Count > 0 || Domains.Count > 0 || Patterns.Count > 0 || Traits.Count > 0;

    /// <summary>
    /// The identities in <paramref name="manifest"/> this filter selects, in manifest order, or the
    /// reason it refuses. Every identity when nothing narrows.
    /// </summary>
    public SpecCoordinateResolution Resolve(SpecManifest manifest)
    {
        if (!NarrowsAnything) return SpecCoordinateResolution.Chose(manifest.Identities);

        var traits = new List<(string Name, string Value)>();
        foreach (var written in Traits)
        {
            var equals = written.IndexOf('=');
            if (equals <= 0 || equals == written.Length - 1)
                return SpecCoordinateResolution.Refused($"--trait '{written}' is not Name=Value.");

            traits.Add((written[..equals].Trim(), written[(equals + 1)..].Trim()));
        }

        var problems = new List<string>();
        unknown("slice", Slices, entry => entry.Slice);
        unknown("chapter", Chapters, entry => entry.Chapter);
        unknown("domain", Domains, entry => entry.Domain);
        unknown("pattern", Patterns, entry => entry.Pattern);

        foreach (var trait in traits)
        {
            if (manifest.Specs.Any(entry => hasTrait(entry, trait.Name, trait.Value))) continue;

            var known = manifest.Specs.SelectMany(entry => traitsOf(entry))
                .Where(x => same(x.Key, trait.Name))
                .Select(x => x.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            problems.Add(known.Count > 0
                ? $"No specification has the trait {trait.Name}={trait.Value}. {trait.Name} is one of: {string.Join(", ", known)}."
                : $"No specification has a trait named '{trait.Name}'.");
        }

        if (problems.Count > 0) return SpecCoordinateResolution.Refused(string.Join(Environment.NewLine, problems));

        var chosen = manifest.Specs
            .Where(entry => matches(Slices, entry.Slice)
                            && matches(Chapters, entry.Chapter)
                            && matches(Domains, entry.Domain)
                            && matches(Patterns, entry.Pattern)
                            && traits.All(trait => hasTrait(entry, trait.Name, trait.Value)))
            .Select(entry => entry.Identity)
            .ToList();

        return chosen.Count == 0
            ? SpecCoordinateResolution.Refused($"No specification is {describe(traits)}: each of those exists, but never together.")
            : SpecCoordinateResolution.Chose(chosen);

        void unknown(string noun, IReadOnlyList<string> wanted, Func<SpecManifestEntry, string?> read)
        {
            var known = manifest.Specs.Select(read).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var value in wanted.Where(value => !known.Any(x => same(x, value))))
            {
                problems.Add(known.Count > 0
                    ? $"No specification is bound to the {noun} '{value}'. The {noun}s with specifications are: {string.Join(", ", known)}."
                    : $"No specification is bound to the {noun} '{value}' — no specification in {manifest.Suite} names a {noun} at all.");
            }
        }
    }

    private string describe(IReadOnlyList<(string Name, string Value)> traits)
    {
        var parts = new List<string>();
        add("slice", Slices);
        add("chapter", Chapters);
        add("domain", Domains);
        add("pattern", Patterns);
        parts.AddRange(traits.Select(x => $"{x.Name}={x.Value}"));
        return string.Join(" and ", parts);

        void add(string noun, IReadOnlyList<string> values)
        {
            if (values.Count > 0) parts.Add($"{noun} {string.Join(" or ", values.Select(x => $"'{x}'"))}");
        }
    }

    private static bool matches(IReadOnlyList<string> wanted, string? actual)
        => wanted.Count == 0 || (actual is not null && wanted.Any(x => same(x, actual)));

    private static bool hasTrait(SpecManifestEntry entry, string name, string value)
        => traitsOf(entry).Any(x => same(x.Key, name) && same(x.Value, value));

    /// <summary>An entry's declared traits plus its coordinates under their trait names.</summary>
    private static IEnumerable<KeyValuePair<string, string>> traitsOf(SpecManifestEntry entry)
    {
        foreach (var coordinate in SpecCoordinates.Of(entry)?.ToTraits() ?? []) yield return coordinate;

        foreach (var (name, values) in entry.Traits ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            foreach (var value in values) yield return new KeyValuePair<string, string>(name, value);
        }
    }

    private static bool same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a <see cref="SpecCoordinateFilter"/> chose, or why it refused.</summary>
public sealed record SpecCoordinateResolution(IReadOnlyList<string> Identities, string? Refusal)
{
    public bool IsRefused => Refusal is not null;

    public static SpecCoordinateResolution Chose(IReadOnlyList<string> identities) => new(identities, null);

    public static SpecCoordinateResolution Refused(string reason) => new([], reason);
}
