namespace Bobcat.Runtime;

/// <summary>
/// Where a specification sits on the Event Model (bobcat#573): its slice, the slice's chapter and
/// domain, and the slice's pattern — the coordinates <c>bobcat run --slice</c> selects by and the
/// runner adapters project as platform traits.
/// </summary>
/// <remarks>
/// <para>
/// <b>The generated binding is the single source.</b> The generator folds a test's
/// <c>[BobcatSpec]</c>/<c>[BobcatSlice]</c> settings and its vocabulary roles into slices exactly as
/// it does for the Event Model descriptor, and binds each identity with its slice's values — so the
/// manifest, the traits and the model cannot disagree. That is why <see cref="Pattern"/> is the
/// <em>derived</em> pattern when nothing declared one, and why <see cref="Chapter"/> and
/// <see cref="Domain"/> are the slice's (first claim wins), not necessarily what this one test wrote.
/// </para>
/// <para>
/// Any of them may be null: a slice with no chapter, or a pattern the specifications cannot tell
/// (Automation and Translation are never inferred). A test that is bound to no slice and states no
/// roles has no coordinates at all.
/// </para>
/// </remarks>
public sealed record SpecCoordinates(string? Slice, string? Chapter, string? Domain, string? Pattern)
{
    /// <summary>The trait (xUnit), property (TUnit, NUnit) name each coordinate is projected under.</summary>
    public const string SliceTrait = "Slice";

    public const string ChapterTrait = "Chapter";

    public const string DomainTrait = "Domain";

    public const string PatternTrait = "Pattern";

    /// <summary>True when there is nothing to say: no slice, chapter, domain or pattern.</summary>
    public bool IsEmpty => Slice is null && Chapter is null && Domain is null && Pattern is null;

    /// <summary>
    /// The coordinates as trait pairs, in a fixed order, leaving out the ones that are null — a trait
    /// with an empty value would select on "unknown" as if it were a name.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToTraits()
    {
        var traits = new List<KeyValuePair<string, string>>();
        add(SliceTrait, Slice);
        add(ChapterTrait, Chapter);
        add(DomainTrait, Domain);
        add(PatternTrait, Pattern);
        return traits;

        void add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value)) traits.Add(new KeyValuePair<string, string>(name, value!));
        }
    }

    /// <summary>The coordinates <paramref name="entry"/> carries, or null when it carries none.</summary>
    public static SpecCoordinates? Of(SpecManifestEntry entry)
    {
        var coordinates = new SpecCoordinates(entry.Slice, entry.Chapter, entry.Domain, entry.Pattern);
        return coordinates.IsEmpty ? null : coordinates;
    }
}
