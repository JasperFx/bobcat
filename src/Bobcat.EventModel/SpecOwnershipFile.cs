namespace Bobcat.EventModel;

/// <summary>Whether a slice's specifications go through the database (issue #324).</summary>
public enum SpecKind
{
    /// <summary>The default: specs boot the store and run the slice end to end.</summary>
    Integration,

    /// <summary>Specs run in memory against the types directly — no store, no host.</summary>
    Unit
}

/// <summary>How a slice's specifications are written (issue #324). Orthogonal to <see cref="SpecKind"/>.</summary>
public enum SpecAuthoring
{
    /// <summary>A <c>.feature</c> file against the shipped grammar. The default.</summary>
    Gherkin,

    /// <summary>A <c>Specification</c> subclass with <c>[Scenario]</c> methods.</summary>
    CodeFirst,

    /// <summary>
    /// An ordinary xUnit/TUnit test rendered through marker steps and bound with
    /// <c>[BobcatSlice]</c> (issue #110, #324 part 1).
    /// </summary>
    Projected
}

/// <summary>
/// The spec-ownership manifest (issue #324 part 4) — which slices are specified somewhere other
/// than the <c>.feature</c> the scaffolder would otherwise write, and where.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate file, not a field on the slice.</b> Moving a test is a change to where work
/// lives, not to the design record: putting it on <see cref="CuratedSlice"/> would churn the event
/// model — and its byte-for-byte regeneration claim — every time a suite is reorganized. The two
/// files join on <see cref="Model"/>, the same merge key everything else folds by.
/// </para>
/// <para>
/// <b>Absent means Gherkin.</b> A slice this file does not list keeps today's behaviour exactly,
/// so the manifest is purely additive: adopting it cannot silently change what an existing repo
/// scaffolds, and CritterCrush needs three entries rather than nineteen.
/// </para>
/// <para>
/// <b>Scenario names stay in the model.</b> This file says only <i>where</i> a slice is specified
/// and <i>in what kind</i>; the <c>{Feature}/{Scenario}</c> identities stay on the curated model,
/// so a Stoat spec-identity gate reads identities from the model and location from here. That
/// split is what makes a second file worth having rather than a second copy of the model.
/// </para>
/// <para>
/// <b>It cannot be derived, so it is validated.</b> A manifest keyed on slice names records a
/// human choice, which means it rots the way CritterCrush's hand-written Stoat plan did — eleven
/// spec identities matching no scenario, silently, because nothing joined them. That plan could be
/// fixed by deriving it; this one cannot, so <see cref="SpecOwnershipReader"/> checks every join
/// it has both halves of.
/// </para>
/// </remarks>
public sealed class SpecOwnershipFile
{
    /// <summary>Format version. Only <c>1</c> is understood today.</summary>
    public int Schema { get; set; }

    /// <summary>
    /// The Event Model this manifest belongs to. Must match the curated model's <c>model:</c> —
    /// the same merge key — or the manifest is describing some other diagram's slices.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    public List<SpecOwnership> Slices { get; set; } = [];
}

/// <summary>One slice's declared spec ownership. Keyed by slice name, the pipeline's merge key.</summary>
public sealed class SpecOwnership
{
    public string Slice { get; set; } = string.Empty;

    /// <summary>
    /// <c>unit</c> | <c>integration</c>. Read as a string and validated here so a typo gets a named
    /// problem rather than a serializer stack trace, exactly as the curated format does.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary><c>gherkin</c> | <c>code-first</c> | <c>projected</c>. See <see cref="Authoring"/>.</summary>
    public string? Authoring { get; set; }

    /// <summary>
    /// The type that owns the specs — <c>CritterCrush.Specs.ProposalSpecs</c>. Where the
    /// scaffolder writes a skeleton, and what the backward join looks for a <c>[BobcatSlice]</c>
    /// on.
    /// </summary>
    public string? Owner { get; set; }

    /// <summary>
    /// The <c>{Feature}/{Scenario}</c> that runs this slice's command end to end, required when
    /// <see cref="Kind"/> is <c>unit</c>.
    /// </summary>
    /// <remarks>
    /// "A unit-tested slice is fine as long as something runs the command end to end later" is a
    /// good rule that dies the first time somebody deletes that scenario. Naming the cover makes it
    /// checkable. Inferring it is not realistic: the chain from <c>AcceptHomeCheckAssignment</c>
    /// through the bus into <c>ProposeHomeCheckAppointment</c> is not expressible in the model,
    /// which is precisely why the declaration is the honest mechanism.
    /// </remarks>
    public string? CoveredBy { get; set; }

    /// <summary>
    /// The resolved kind — <see cref="SpecKind.Integration"/> when unstated, which is today's
    /// behaviour for every unlisted slice.
    /// </summary>
    public SpecKind ResolvedKind => SpecOwnershipVocabulary.TryParseKind(Kind, out var kind) ? kind : SpecKind.Integration;

    /// <summary>
    /// The resolved authoring style. Unstated means <see cref="SpecAuthoring.Gherkin"/> — except
    /// under <c>kind: unit</c>, where <see cref="SpecAuthoring.Projected"/> is the only pairing the
    /// format permits, so defaulting to Gherkin would make every terse unit entry invalid for
    /// saying nothing.
    /// </summary>
    public SpecAuthoring ResolvedAuthoring
    {
        get
        {
            if (SpecOwnershipVocabulary.TryParseAuthoring(Authoring, out var authoring)) return authoring;
            return ResolvedKind == SpecKind.Unit ? SpecAuthoring.Projected : SpecAuthoring.Gherkin;
        }
    }

    /// <summary>True when the scaffolder should NOT write this slice's scenarios into a <c>.feature</c>.</summary>
    public bool SuppressesFeature => ResolvedAuthoring != SpecAuthoring.Gherkin;
}

/// <summary>
/// Parsing for the manifest's two closed vocabularies, in one place because the generator has to
/// speak them too (see <c>Bobcat.Generators.SpecOwnershipManifest</c>) and a silent divergence
/// between the two would put a slice in one lane here and the other there.
/// </summary>
public static class SpecOwnershipVocabulary
{
    public static bool TryParseKind(string? value, out SpecKind kind)
    {
        kind = SpecKind.Integration;
        return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(Normalize(value!), ignoreCase: true, out kind);
    }

    public static bool TryParseAuthoring(string? value, out SpecAuthoring authoring)
    {
        authoring = SpecAuthoring.Gherkin;
        return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(Normalize(value!), ignoreCase: true, out authoring);
    }

    /// <summary>
    /// <c>code-first</c> is the spelling the file uses and <c>CodeFirst</c> the one the enum uses;
    /// hyphens and underscores are dropped so the YAML reads like YAML.
    /// </summary>
    public static string Normalize(string value) => value.Trim().Replace("-", "").Replace("_", "");

    public static readonly string[] KindNames = ["integration", "unit"];

    public static readonly string[] AuthoringNames = ["gherkin", "code-first", "projected"];
}
