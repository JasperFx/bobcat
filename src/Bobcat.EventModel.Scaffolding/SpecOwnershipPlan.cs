namespace Bobcat.EventModel.Scaffolding;

/// <summary>
/// The spec-ownership manifest resolved per slice (issue #324 part 4) — the single place the
/// scaffolder asks "what does this slice's specification look like, and do I write it".
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="None"/> is the whole compatibility story.</b> Every lookup on it answers
/// Gherkin/integration, which is exactly what the scaffolder did before this type existed, so a
/// caller that passes no manifest gets byte-for-byte what it got before — and so does a slice the
/// manifest does not list.
/// </para>
/// <para>
/// Resolved once and read by every emitter, for the reason <see cref="SlicePlan"/> is: two halves
/// of the scaffolder deriving the same decision separately is how a slice ended up with a
/// <c>.feature</c> AND a skeleton claiming one identity, which is the collision this manifest
/// exists to prevent (issue #231 is the same lesson in the other direction).
/// </para>
/// </remarks>
public sealed class SpecOwnershipPlan
{
    private readonly Dictionary<string, SpecOwnership> _entries;

    private SpecOwnershipPlan(Dictionary<string, SpecOwnership> entries) => _entries = entries;

    /// <summary>No manifest: every slice is an integration slice specified in Gherkin.</summary>
    public static SpecOwnershipPlan None { get; } = new([]);

    public static SpecOwnershipPlan For(SpecOwnershipFile? manifest)
    {
        if (manifest is null || manifest.Slices.Count == 0) return None;

        var entries = new Dictionary<string, SpecOwnership>(StringComparer.Ordinal);
        foreach (var entry in manifest.Slices)
        {
            if (string.IsNullOrWhiteSpace(entry.Slice)) continue;

            // First wins. A duplicate is already a validation problem; silently taking the last
            // would make a rejected file still change the output if someone scaffolded anyway.
            if (!entries.ContainsKey(entry.Slice)) entries[entry.Slice] = entry;
        }

        return new SpecOwnershipPlan(entries);
    }

    public SpecOwnership? EntryFor(string sliceName)
        => _entries.TryGetValue(sliceName, out var entry) ? entry : null;

    public SpecKind KindFor(string sliceName) => EntryFor(sliceName)?.ResolvedKind ?? SpecKind.Integration;

    public SpecAuthoring AuthoringFor(string sliceName)
        => EntryFor(sliceName)?.ResolvedAuthoring ?? SpecAuthoring.Gherkin;

    /// <summary>Whether this slice's scenarios belong in a <c>.feature</c> file.</summary>
    public bool ScaffoldsFeature(string sliceName) => AuthoringFor(sliceName) == SpecAuthoring.Gherkin;

    /// <summary>The slices this manifest takes out of the Gherkin lane, grouped by the type that owns them.</summary>
    public IEnumerable<IGrouping<string, SpecOwnership>> ByOwner()
        => _entries.Values
            .Where(x => x.SuppressesFeature && !string.IsNullOrWhiteSpace(x.Owner))
            .GroupBy(x => x.Owner!, StringComparer.Ordinal);
}
