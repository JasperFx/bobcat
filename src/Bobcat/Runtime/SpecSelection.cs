namespace Bobcat.Runtime;

/// <summary>
/// The specifications a run was asked for, as a set of <see cref="SpecIdentity"/> strings
/// (issue #391) — so a monitor only ever names identities and never a framework's own filter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Empty means everything.</b> A selection that narrows nothing is the ordinary unfiltered
/// run, which keeps the type usable as the single parameter on a run path rather than something
/// a caller only passes sometimes. <see cref="NarrowsAnything"/> is how a caller tells the two
/// apart when it has to — a resident runner must, because MTP silently ignores a subset parameter
/// it does not understand and runs the whole suite, which is indistinguishable from a filter that
/// matched everything (the supervisor's <c>GuardAgainstAnUnfilteredRun</c> exists for exactly
/// that).
/// </para>
/// <para>
/// <b>It does not resolve anything.</b> A selection is a request, and a request can name a
/// specification the suite does not have. <see cref="NotIn"/> is how a runner says so — by
/// name, before running — rather than running a narrowed suite that silently matched nothing.
/// </para>
/// </remarks>
public sealed class SpecSelection
{
    /// <summary>A selection that narrows nothing: the ordinary whole-suite run.</summary>
    public static readonly SpecSelection Everything = new([]);

    private readonly HashSet<string> _identities;

    private SpecSelection(IReadOnlyList<string> identities)
    {
        Identities = identities;
        _identities = new HashSet<string>(identities, SpecIdentity.Comparer);
    }

    /// <summary>
    /// The identities asked for, in the order they were asked for and without duplicates. Order
    /// is kept because it is the order a reason or a report lists them in, and an arbitrary
    /// reordering of someone's request reads as a bug.
    /// </summary>
    public IReadOnlyList<string> Identities { get; }

    /// <summary>False for <see cref="Everything"/> — nothing was asked for in particular.</summary>
    public bool NarrowsAnything => Identities.Count > 0;

    public static SpecSelection Of(params string[] identities) => Of((IEnumerable<string>)identities);

    /// <summary>
    /// A selection of the identities given. Blank entries are dropped rather than kept as an
    /// identity nothing can match, and duplicates collapse.
    /// </summary>
    public static SpecSelection Of(IEnumerable<string> identities)
    {
        var seen = new HashSet<string>(SpecIdentity.Comparer);
        var ordered = new List<string>();

        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity)) continue;
            if (seen.Add(identity)) ordered.Add(identity);
        }

        return ordered.Count == 0 ? Everything : new SpecSelection(ordered);
    }

    /// <summary>Whether this selection admits a specification. Always true when it narrows nothing.</summary>
    public bool Includes(string identity) => !NarrowsAnything || _identities.Contains(identity);

    /// <summary>Whether this selection admits a scenario, by the titles it is identified from.</summary>
    public bool Includes(string featureTitle, string scenarioTitle)
        => Includes(SpecIdentity.Of(featureTitle, scenarioTitle));

    /// <summary>
    /// The identities asked for that are not among <paramref name="known"/> — what a runner
    /// rejects, and names in the reason it rejects with.
    /// </summary>
    public IReadOnlyList<string> NotIn(IEnumerable<string> known)
    {
        if (!NarrowsAnything) return [];

        var available = new HashSet<string>(known, SpecIdentity.Comparer);
        return Identities.Where(identity => !available.Contains(identity)).ToList();
    }

    public override string ToString()
        => NarrowsAnything ? string.Join(", ", Identities) : "(every specification)";
}
