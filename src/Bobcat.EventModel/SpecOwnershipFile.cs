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

    /// <summary>
    /// What every slice this file does not state otherwise looks like (issue #334). Absent, the
    /// built-in defaults apply and an unlisted slice is a Gherkin integration slice — exactly
    /// today's behaviour.
    /// </summary>
    public SpecOwnershipDefaults? Defaults { get; set; }

    public List<SpecOwnership> Slices { get; set; } = [];

    /// <summary>The entry for a slice, or null when this file does not list it.</summary>
    public SpecOwnership? EntryFor(string slice)
        => Slices.FirstOrDefault(x => string.Equals(x.Slice, slice, StringComparison.Ordinal));

    /// <summary>
    /// What this file says about one slice, entry over defaults over built-in — the ONE way to
    /// ask (issue #334).
    /// </summary>
    /// <param name="slice">The slice name, listed here or not.</param>
    /// <param name="feature">
    /// The slice's feature, for a <c>{feature}</c> token in a default owner. The slice name when
    /// null, which is what the curated model's <c>specifications.feature:</c> defaults to.
    /// </param>
    /// <remarks>
    /// The entry's own <see cref="SpecOwnership.StatedKind"/> and
    /// <see cref="SpecOwnership.StatedAuthoring"/> are nullable and mean only what the entry says,
    /// so nothing can answer this question while ignoring <see cref="Defaults"/>. That is the
    /// point: a resolved answer computed in two places is how a slice ends up in one lane for the
    /// scaffolder and another for the analyzer.
    /// </remarks>
    public ResolvedSpecOwnership Resolve(string slice, string? feature = null)
    {
        var entry = EntryFor(slice);

        var kind = entry?.StatedKind ?? Defaults?.StatedKind ?? SpecKind.Integration;

        // An entry's own `kind: unit` implies projected whatever the defaults say, because unit is
        // the one kind the other authoring styles cannot express — both run through the fixture,
        // and so through the store. Without this, `defaults: { authoring: gherkin }` would turn
        // every terse unit entry into a validation problem for saying nothing.
        var authoring = entry?.StatedAuthoring
                        ?? (entry?.StatedKind == SpecKind.Unit ? SpecAuthoring.Projected : (SpecAuthoring?)null)
                        ?? Defaults?.StatedAuthoring
                        ?? (kind == SpecKind.Unit ? SpecAuthoring.Projected : SpecAuthoring.Gherkin);

        var owner = entry?.Owner is { Length: > 0 } stated
            ? stated
            : SpecOwnershipDefaults.ExpandOwner(Defaults?.Owner, slice, feature ?? slice);

        return new ResolvedSpecOwnership(
            slice,
            kind,
            authoring,
            Scaffold: scaffolds(entry, kind, authoring),
            Owner: owner,
            CoveredBy: entry?.CoveredBy,
            Listed: entry is not null);
    }

    /// <summary>
    /// Whether the scaffolder writes this slice's specification. Stated wins; otherwise everything
    /// is scaffolded EXCEPT the one corner where the format refuses to guess — see
    /// <see cref="ResolvedSpecOwnership.NeedsScaffoldStated"/>, which the reader reports as a
    /// problem. Falling back to false there keeps the pre-#334 behaviour for a file that ignores
    /// the problem, rather than offering to overwrite a suite that may already exist.
    /// </summary>
    private bool scaffolds(SpecOwnership? entry, SpecKind kind, SpecAuthoring authoring)
    {
        if (entry?.Scaffold is { } stated) return stated;
        if (Defaults?.Scaffold is { } inherited) return inherited;

        return !(authoring == SpecAuthoring.Projected && kind == SpecKind.Integration);
    }
}

/// <summary>
/// The manifest's file-level defaults (issue #334) — what a slice looks like unless its own entry
/// says otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The manifest was designed around "three entries, not nineteen": absent
/// means Gherkin, so listing the exceptions is cheap. A repo built model-first inverts that —
/// CritterCrush's Lane A is 19 slices, 18 of them projected integration tests — and without a
/// file-level default the exception has to be written nineteen times, with nineteen
/// near-identical warnings to match.
/// </para>
/// <para>
/// <b>Absent still means Gherkin.</b> A manifest with no <c>defaults:</c> resolves exactly as it
/// did before this existed, so nothing an existing repo scaffolds changes.
/// </para>
/// <para>
/// <b>What it deliberately does NOT default.</b> <c>coveredBy:</c> is per slice by nature — it
/// names one scenario — and a defaulted one would claim the same cover for every unit slice in the
/// file.
/// </para>
/// </remarks>
public sealed class SpecOwnershipDefaults
{
    /// <inheritdoc cref="SpecOwnership.Kind"/>
    public string? Kind { get; set; }

    /// <inheritdoc cref="SpecOwnership.Authoring"/>
    public string? Authoring { get; set; }

    /// <inheritdoc cref="SpecOwnership.Scaffold"/>
    public bool? Scaffold { get; set; }

    /// <summary>
    /// What an INTEGRATION spec class needs in order to reach the store (issue #356): the base type,
    /// optionally a fixture to inject, optionally an attribute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scaffolder already knows a class holds integration slices — the manifest told it — so the
    /// only thing it could not write was which fixture THIS repository boots a store with. That is
    /// one answer per repo, which is what makes it a default rather than a comment telling every
    /// reader of every generated class to go and look it up.
    /// </para>
    /// <para>
    /// Three literals rather than one convention. A single <c>fixture:</c> string would have to
    /// assume how the base is constructed and how the test framework names collections, and
    /// CritterCrush's shape — <c>[Collection(CritterCrushHost.CollectionName)]</c> with the host
    /// injected and forwarded — is one of several an xUnit repo might reasonably use. Each field
    /// here maps to exactly one piece of the declaration and none of them is guessed.
    /// </para>
    /// </remarks>
    public SpecFixture? Fixture { get; set; }

    /// <summary>
    /// A template for the type that owns each slice's specs, expanded per slice:
    /// <c>{feature}</c> is the slice's feature (the slice name when the model states none) and
    /// <c>{slice}</c> is the slice name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A template rather than a literal because <c>owner:</c> is the one field that is genuinely
    /// per slice, so a literal default would leave an all-projected repo writing nineteen entries
    /// anyway — the verbosity this block exists to remove. <c>{feature}</c> is the useful token:
    /// it groups slices exactly as the scaffolder already groups skeletons, which is what
    /// CritterCrush wrote by hand.
    /// </para>
    /// <para>
    /// The same token convention the curated format already uses for <c>{streamId}</c>. An unknown
    /// token is a validation problem, never a literal left in a type name.
    /// </para>
    /// </remarks>
    public string? Owner { get; set; }

    /// <inheritdoc cref="SpecOwnership.StatedKind"/>
    public SpecKind? StatedKind
        => SpecOwnershipVocabulary.TryParseKind(Kind, out var kind) ? kind : null;

    /// <inheritdoc cref="SpecOwnership.StatedAuthoring"/>
    public SpecAuthoring? StatedAuthoring
        => SpecOwnershipVocabulary.TryParseAuthoring(Authoring, out var authoring) ? authoring : null;

    /// <summary>The tokens an owner template may use.</summary>
    public static IReadOnlyList<string> OwnerTokens { get; } = ["feature", "slice"];

    /// <summary>
    /// A default owner template expanded for one slice, or null when there is no template.
    /// </summary>
    public static string? ExpandOwner(string? template, string slice, string feature)
        => template is not { Length: > 0 }
            ? null
            : template.Replace("{feature}", feature).Replace("{slice}", slice);

    /// <summary>The <c>{tokens}</c> in a template that are not <see cref="OwnerTokens"/>.</summary>
    public static IEnumerable<string> UnknownOwnerTokens(string? template)
    {
        if (template is not { Length: > 0 }) yield break;

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') continue;

            var close = template.IndexOf('}', i + 1);
            if (close < 0) break;

            var token = template.Substring(i + 1, close - i - 1);
            i = close;

            if (!OwnerTokens.Contains(token)) yield return token;
        }
    }
}

/// <summary>
/// What the manifest says about one slice, with <see cref="SpecOwnershipDefaults"/> already
/// applied (issue #334).
/// </summary>
/// <param name="Scaffold">Whether the scaffolder writes this slice's specification at all.</param>
/// <param name="Listed">Whether the manifest names this slice explicitly.</param>
public sealed record ResolvedSpecOwnership(
    string Slice,
    SpecKind Kind,
    SpecAuthoring Authoring,
    bool Scaffold,
    string? Owner,
    string? CoveredBy,
    bool Listed)
{
    /// <summary>The built-in answer for a manifest that says nothing: a Gherkin integration slice.</summary>
    public static ResolvedSpecOwnership Default(string slice)
        => new(slice, SpecKind.Integration, SpecAuthoring.Gherkin, Scaffold: true, null, null, Listed: false);

    /// <summary>True when the scaffolder should NOT write this slice's scenarios into a <c>.feature</c>.</summary>
    public bool SuppressesFeature => Authoring != SpecAuthoring.Gherkin;

    /// <summary>
    /// The one corner where <c>scaffold:</c> has to be stated (issue #334): a projected
    /// integration slice.
    /// </summary>
    /// <remarks>
    /// The row conflated two intents. "An existing hand-written suite adopts this slice" — Marten's
    /// DaemonTests, where the tests predate the model and generating would overwrite them — and
    /// "generate me an integration test, authored as a projected test", which is every slice of a
    /// repo being built. Both spell themselves <c>projected</c> + <c>integration</c>, and the
    /// difference is not derivable: the scaffolder is a CLI with no compilation and no view of the
    /// disk, so it can see neither whether a type binds the slice nor whether a file exists.
    /// Defaulting either way fails silently in the case it is wrong — dropping a suite's worth of
    /// tests, or offering to overwrite one — so the format asks.
    /// </remarks>
    public bool NeedsScaffoldStated => Authoring == SpecAuthoring.Projected && Kind == SpecKind.Integration;
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
    /// Whether the scaffolder writes this slice's specification (issue #334) — the difference
    /// between "generate me a test" and "an existing suite already covers this". Required for a
    /// projected integration slice, where the two are otherwise indistinguishable; inherited from
    /// <see cref="SpecOwnershipDefaults.Scaffold"/> or assumed elsewhere.
    /// </summary>
    public bool? Scaffold { get; set; }

    /// <summary>
    /// The kind this ENTRY states, or null when it says nothing. Nullable on purpose: the answer
    /// that matters is <see cref="SpecOwnershipFile.Resolve"/>'s, and a property here that quietly
    /// substituted a built-in default would be a second answer that ignores the file's
    /// <c>defaults:</c>.
    /// </summary>
    public SpecKind? StatedKind
        => SpecOwnershipVocabulary.TryParseKind(Kind, out var kind) ? kind : null;

    /// <inheritdoc cref="StatedKind"/>
    public SpecAuthoring? StatedAuthoring
        => SpecOwnershipVocabulary.TryParseAuthoring(Authoring, out var authoring) ? authoring : null;
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

/// <summary>
/// How an integration spec class is declared, so the scaffolder can write it instead of leaving a
/// TODO (issue #356).
/// </summary>
public sealed class SpecFixture
{
    /// <summary>The base type an integration spec class derives from. Required when this block is present.</summary>
    public string? BaseType { get; set; }

    /// <summary>
    /// A fixture type taken as a primary-constructor parameter and forwarded to the base — the
    /// xUnit collection-fixture shape. Absent, the class simply derives.
    /// </summary>
    public string? Inject { get; set; }

    /// <summary>
    /// An attribute to put on the class, written out in full, e.g.
    /// <c>Collection(CritterCrushHost.CollectionName)</c>. A literal because a test framework's
    /// collection naming is not something this format should pretend to know.
    /// </summary>
    public string? Attribute { get; set; }
}
