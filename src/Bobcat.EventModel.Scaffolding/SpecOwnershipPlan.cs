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
    private readonly SpecOwnershipFile? _manifest;

    private SpecOwnershipPlan(SpecOwnershipFile? manifest) => _manifest = manifest;

    /// <summary>No manifest: every slice is an integration slice specified in Gherkin.</summary>
    public static SpecOwnershipPlan None { get; } = new(null);

    public static SpecOwnershipPlan For(SpecOwnershipFile? manifest)
        => manifest is null || (manifest.Slices.Count == 0 && manifest.Defaults is null)
            ? None
            : new SpecOwnershipPlan(manifest);

    /// <summary>
    /// What the manifest says about one slice, defaults applied (issue #334). Resolution lives on
    /// <see cref="SpecOwnershipFile.Resolve"/> so the analyzer's copy has one shape to agree with,
    /// and this type stays the scaffolder's door onto it.
    /// </summary>
    /// <param name="feature">
    /// The slice's feature, for a <c>{feature}</c> token in <c>defaults.owner:</c>. Pass it
    /// wherever the model is in hand; the slice name is the fallback, which is what the model's
    /// own <c>specifications.feature:</c> defaults to.
    /// </param>
    public ResolvedSpecOwnership Resolve(string sliceName, string? feature = null)
        => _manifest?.Resolve(sliceName, feature) ?? ResolvedSpecOwnership.Default(sliceName);

    /// <summary>The entry for a slice, or null when the manifest does not list it.</summary>
    public SpecOwnership? EntryFor(string sliceName) => _manifest?.EntryFor(sliceName);

    /// <summary>
    /// How an integration spec class reaches the store (issue #356), or null when the manifest does
    /// not say — in which case the skeleton keeps the TODO that asks for it.
    /// </summary>
    public SpecFixture? Fixture => _manifest?.Defaults?.Fixture;

    public SpecKind KindFor(string sliceName) => Resolve(sliceName).Kind;

    public SpecAuthoring AuthoringFor(string sliceName) => Resolve(sliceName).Authoring;

    /// <summary>Whether this slice's scenarios belong in a <c>.feature</c> file.</summary>
    public bool ScaffoldsFeature(string sliceName) => Resolve(sliceName).Authoring == SpecAuthoring.Gherkin;

    /// <summary>
    /// The slices to write a spec skeleton for, grouped by the type that owns them (issue #334).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven by the MODEL's slices rather than the manifest's entries, which is the change #334
    /// needed: with a <c>defaults:</c> block the slices to scaffold are mostly the ones with no
    /// entry at all, so walking the entries found one of nineteen.
    /// </para>
    /// <para>
    /// A slice is here when it is out of the Gherkin lane, <c>scaffold:</c> resolves true, and
    /// something names an owner to write it into. Ordered by the model, so a regenerated skeleton
    /// is byte-identical.
    /// </para>
    /// </remarks>
    public IEnumerable<IGrouping<string, ResolvedSpecOwnership>> OwnersIn(CuratedModelFile model)
        => model.Slices
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => Resolve(x.Name, x.Specifications?.Feature ?? x.Name))
            .Where(x => x.SuppressesFeature && x.Scaffold && x.Owner is { Length: > 0 })
            .GroupBy(x => x.Owner!, StringComparer.Ordinal);
}
