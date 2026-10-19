using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bobcat.Runtime;

/// <summary>
/// What a suite says it specifies, listed without running anything (issue #391): every
/// <see cref="SpecIdentity"/> it has, and — for a projected suite — the test method each one is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a file and not stdout.</b> The listing has to cross a process boundary: a resident
/// runner (issue #390) is not the suite, and for a projected suite it cannot be — the test
/// framework owns that process. Standard output is already the framework's, so a manifest printed
/// there would have to be fished back out of discovery chatter. A path the asker nominates in
/// <see cref="PathVariable"/> is unambiguous, and nothing is written unless something asked.
/// </para>
/// <para>
/// <b>Why not just read <c>--list-tests</c>.</b> Because its output is display names, not
/// identities. A projected host prints the test framework's own name for a method, which is not the
/// string a monitor must send back.
/// </para>
/// <para>
/// <b>It is written at the only moment it can be.</b> A projected suite writes it from the generated
/// module initializer, because that is the only code Bobcat owns in that process — so the caller
/// pairs the variable with <c>--list-tests</c> and the framework runs nothing.
/// </para>
/// </remarks>
/// <param name="Lane">
/// <c>projected</c>, the only lane since the Gherkin lane was removed (bobcat#471). Kept on the
/// document so a reader of an older manifest (<c>gherkin</c>) can still tell them apart.
/// </param>
/// <param name="Framework">
/// Which runner owns the process — <c>xunit</c>, <c>tunit</c>, <c>nunit</c> or <c>mstest</c>. The lane does not
/// settle it: a projected suite's filter spelling is its *framework's*, and two projected suites
/// can disagree about it.
/// </param>
/// <param name="Suite">The suite's name, as <c>run_started</c> reports it.</param>
/// <param name="Specs">
/// Every specification, sorted by identity. Sorted rather than in enumeration order because this
/// is a set — what a runner registers and checks membership against — and a listing that reshuffled
/// between two discoveries of the same suite would read as a change.
/// </param>
/// <param name="RecoveryHints">
/// The recovery hints the suite declares on itself through <c>Bobcat.Resilience</c>'s
/// <c>[ClearsOnRetry]</c>, <c>[ClearsOnRecycle]</c>, <c>[ClearsInFreshProcess]</c> and
/// <c>[NeverRecovers]</c> (issue #469), at assembly, class and method scope. Null — and so absent
/// from the document — when it declares none, or when the lane does not report them. Carried here
/// because a supervisor drives the suite as a process and never loads its assembly, and this is
/// already the document a suite writes about itself when asked. Additive on purpose: a reader
/// that predates the field ignores it, and a manifest without it reads as "no hints".
/// </param>
public sealed record SpecManifest(
    string Lane,
    string Framework,
    string Suite,
    IReadOnlyList<SpecManifestEntry> Specs,
    IReadOnlyList<SpecManifestHint>? RecoveryHints = null)
{
    /// <summary>
    /// Set this to a file path and the suite writes its manifest there instead of keeping the
    /// listing to itself. Read once, by the suite, at the moment it can answer.
    /// </summary>
    public const string PathVariable = "BOBCAT_LIST_SPECS";

    public const string ProjectedLane = "projected";

    public const string XunitFramework = "xunit";
    public const string TUnitFramework = "tunit";
    public const string NUnitFramework = "nunit";
    public const string MSTestFramework = "mstest";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,

        // By name, not by number: a hint's kind and scope are read by people triaging a run as
        // often as by a supervisor, and an enum's ordinal is not a contract anyone promised.
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Every identity two or more tests claim (bobcat#489), each with all of its claimants. Null --
    /// and so absent from the document -- when every identity names one test, which is the only
    /// healthy state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Specs"/> still carries one entry per identity, the first test that bound it, because
    /// it is a set and a reader that predates this field must keep reading it as one. This is where
    /// the others are, and <see cref="SpecFilterArguments"/> refuses to run a listed identity by name
    /// rather than run whichever test happened to be kept.
    /// </para>
    /// <para>
    /// BOBCAT036 refuses duplicates at build time within one compilation; what reaches a manifest is
    /// what the compiler could not see together -- specs in two assemblies loaded into one process -- or
    /// the error suppressed. Additive, like <see cref="RecoveryHints"/>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SpecManifestDuplicate>? Duplicates { get; init; }

    /// <summary>Whether more than one test claims <paramref name="identity"/> (bobcat#489).</summary>
    public bool IsAmbiguous(string identity)
        => Duplicates?.Any(x => SpecIdentity.Same(x.Identity, identity)) == true;

    /// <summary>
    /// One line per duplicated identity, saying which tests share it and what to do about it -- what a
    /// manifest write prints on standard error. Empty when there are no duplicates.
    /// </summary>
    public IReadOnlyList<string> DescribeDuplicates()
        => (Duplicates ?? [])
            .Select(duplicate =>
                $"Bobcat: the spec identity '{duplicate.Identity}' in {Suite} is claimed by {duplicate.Claims.Count} tests: "
                + string.Join(", ", duplicate.Claims.Select(x => x.QualifiedTestMethod ?? x.Identity))
                + ". Identity filtering, the resident runner, the flake/duration ledger and the Event Model treat "
                + "them as one specification, so running it by identity is refused. Rename one of the test "
                + "methods, or give one an explicit scenario name with [BobcatSpec(Name = \"...\")].")
            .ToList();

    /// <summary>Every identity in the manifest — what a resident runner registers as its specs.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Identities => Specs.Select(spec => spec.Identity).ToList();

    /// <summary>The entry for an identity, or null when this suite does not have it.</summary>
    public SpecManifestEntry? For(string identity)
        => Specs.FirstOrDefault(spec => SpecIdentity.Same(spec.Identity, identity));

    public string ToJson() => JsonSerializer.Serialize(this, _json);

    public static SpecManifest? FromJson(string json) => JsonSerializer.Deserialize<SpecManifest>(json, _json);

    /// <summary>Read a manifest a suite wrote.</summary>
    public static SpecManifest Read(string path)
        => FromJson(File.ReadAllText(path))
           ?? throw new InvalidOperationException($"'{path}' is not a Bobcat spec manifest.");

    /// <summary>
    /// The path a caller asked for a manifest at, or null when nobody did.
    /// </summary>
    public static string? RequestedPath()
        => Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } path ? path : null;

    /// <summary>
    /// Write the manifest if — and only if — something asked for one. The manifest is built
    /// lazily so a suite that was not asked pays nothing for the question.
    /// </summary>
    /// <remarks>
    /// <b>It cannot fail the suite.</b> This is a courtesy to a listener, exactly like the monitor
    /// publisher's probe: an unwritable path is the asker's mistake, and a test run that collapsed
    /// because a listing could not be saved would be a far worse bug than a listing that did not
    /// arrive. The exception is reported on standard error and swallowed.
    /// <para>
    /// <b>Duplicate identities are reported, and the manifest is still written</b> (bobcat#489). One
    /// line per duplicate goes to standard error, and the document carries them as
    /// <see cref="Duplicates"/>. Refusing the write would cost every other specification in the suite
    /// its listing -- a resident runner with no manifest cannot run anything -- to protect one identity
    /// that <see cref="SpecFilterArguments"/> already refuses to run by name. The build-time error is
    /// where a duplicate is meant to stop; this is the backstop for what the compiler could not see.
    /// </para>
    /// </remarks>
    public static bool WriteIfRequested(Func<SpecManifest> build) => WriteIfRequested(build, Console.Error);

    /// <summary>The same, reporting to <paramref name="errors"/> -- the seam a test reads standard error through.</summary>
    internal static bool WriteIfRequested(Func<SpecManifest> build, TextWriter errors)
    {
        // bobcat#644: a listing that also asked for the outlines gets them at exit, once every
        // module initializer (planned steps included) has registered.
        SpecOutline.ArmIfRequested();

        var path = RequestedPath();
        if (path is null) return false;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var manifest = build();
            foreach (var line in manifest.DescribeDuplicates()) errors.WriteLine(line);

            File.WriteAllText(path, manifest.ToJson());
            return true;
        }
        catch (Exception e)
        {
            errors.WriteLine($"Bobcat could not write its spec manifest to '{path}': {e.Message}");
            return false;
        }
    }
}

/// <summary>
/// One specification in a <see cref="SpecManifest"/>.
/// </summary>
/// <param name="Identity">The <c>{Feature}/{Scenario}</c> string a monitor sends back.</param>
/// <param name="TestClass">
/// The fully qualified class the projected test is declared on, or null in the (removed) Gherkin lane where
/// the identity is itself the platform's uid. This is the fact a framework's filter needs and the
/// identity cannot supply: <see cref="MarkerSpecNaming"/> maps a class name to a feature title but
/// an explicit <c>[BobcatFeature("…")]</c> title has no way back, so the binding is recorded here
/// rather than re-derived.
/// </param>
/// <param name="TestMethod">The projected test's method name.</param>
/// <param name="Parent">
/// The method's identity when this entry is one constant row of a data-driven specification
/// (bobcat#569), else null. A row shares its method's <see cref="TestClass"/> and
/// <see cref="TestMethod"/>, so a method filter cannot pick it out on its own.
/// </param>
/// <param name="Slice">
/// The Event Model slice the specification is bound to (bobcat#573), or null when it is bound to
/// none. With <paramref name="Chapter"/>, <paramref name="Domain"/> and <paramref name="Pattern"/>
/// these are <see cref="SpecCoordinates"/>, written from the generated binding — the same values the
/// runner adapters project as traits. Additive: a manifest without them reads as "no coordinates".
/// </param>
/// <param name="Chapter">The slice's chapter, or null.</param>
/// <param name="Domain">The slice's domain, or null.</param>
/// <param name="Pattern">The slice's pattern, declared or derived, or null when neither says.</param>
public sealed record SpecManifestEntry(
    string Identity,
    string? TestClass = null,
    string? TestMethod = null,
    string? Parent = null,
    string? Slice = null,
    string? Chapter = null,
    string? Domain = null,
    string? Pattern = null)
{
    /// <summary>True when this entry is one row of a data-driven specification (bobcat#569).</summary>
    [JsonIgnore]
    public bool IsRow => Parent is not null;

    /// <summary>
    /// The traits the test declares through its framework's own attributes (bobcat#573) — xUnit's
    /// <c>[Trait]</c> (any <c>ITraitAttribute</c>), TUnit's and NUnit's <c>[Property]</c>, NUnit's
    /// <c>[Category]</c> and MSTest's <c>[TestProperty]</c>/<c>[TestCategory]</c>, on the method, its
    /// class or the assembly — keyed by trait name. Null, and so absent from the document, when it
    /// declares none. What <c>bobcat run --trait</c> selects on, beside the coordinates; the
    /// coordinates themselves are not repeated here.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Traits { get; init; }

    /// <summary>
    /// The fully qualified method name a framework filter takes, or null when this entry carries
    /// no binding.
    /// </summary>
    [JsonIgnore]
    public string? QualifiedTestMethod
        => TestClass is { Length: > 0 } type && TestMethod is { Length: > 0 } method
            ? $"{type}.{method}"
            : null;
}

/// <summary>
/// An identity more than one test claims (bobcat#489), with every test that claims it.
/// </summary>
/// <param name="Identity">The shared <c>{Feature}/{Scenario}</c> string.</param>
/// <param name="Claims">Each test that bound it, sorted by its qualified method name.</param>
public sealed record SpecManifestDuplicate(string Identity, IReadOnlyList<SpecManifestEntry> Claims);
