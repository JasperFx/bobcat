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
/// identities. A Gherkin host prints <c>"Ordering: An order is accepted"</c> while the identity is
/// <c>"Ordering/An order is accepted"</c>, and a projected host prints the test framework's own
/// name for a method — neither is the string a monitor must send back. Deriving one from the other
/// works until a feature title contains <c>": "</c>, which is precisely the kind of silent
/// mismatch a machine identity exists to prevent.
/// </para>
/// <para>
/// <b>Each lane writes it at the only moment it can.</b> A Gherkin host writes it during
/// discovery, where its features are already scanned and nothing is executed. A projected suite
/// writes it from the generated module initializer, because that is the only code Bobcat owns in
/// that process — so the caller pairs the variable with <c>--list-tests</c> and the framework
/// runs nothing. Both are reached by the same request and produce the same document.
/// </para>
/// </remarks>
/// <param name="Lane">
/// <c>gherkin</c> or <c>projected</c>. Only the runner knows its own lane, which is the whole
/// premise of issue #391, so it is stated here rather than inferred by a reader.
/// </param>
/// <param name="Framework">
/// Which runner owns the process — <c>bobcat</c>, <c>xunit</c> or <c>tunit</c>. The lane does not
/// settle it: a projected suite's filter spelling is its *framework's*, and two projected suites
/// can disagree about it.
/// </param>
/// <param name="Suite">The suite's name, as <c>run_started</c> reports it.</param>
/// <param name="Specs">
/// Every specification, sorted by identity. Sorted rather than in enumeration order because this
/// is a set — what a runner registers and checks membership against — and a listing that reshuffled
/// between two discoveries of the same suite would read as a change.
/// </param>
public sealed record SpecManifest(
    string Lane,
    string Framework,
    string Suite,
    IReadOnlyList<SpecManifestEntry> Specs)
{
    /// <summary>
    /// Set this to a file path and the suite writes its manifest there instead of keeping the
    /// listing to itself. Read once, by the suite, at the moment it can answer.
    /// </summary>
    public const string PathVariable = "BOBCAT_LIST_SPECS";

    public const string GherkinLane = "gherkin";
    public const string ProjectedLane = "projected";

    public const string BobcatFramework = "bobcat";
    public const string XunitFramework = "xunit";
    public const string TUnitFramework = "tunit";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

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
    /// </remarks>
    public static bool WriteIfRequested(Func<SpecManifest> build)
    {
        var path = RequestedPath();
        if (path is null) return false;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(path, build().ToJson());
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Bobcat could not write its spec manifest to '{path}': {e.Message}");
            return false;
        }
    }
}

/// <summary>
/// One specification in a <see cref="SpecManifest"/>.
/// </summary>
/// <param name="Identity">The <c>{Feature}/{Scenario}</c> string a monitor sends back.</param>
/// <param name="TestClass">
/// The fully qualified class the projected test is declared on, or null in the Gherkin lane where
/// the identity is itself the platform's uid. This is the fact a framework's filter needs and the
/// identity cannot supply: <see cref="MarkerSpecNaming"/> maps a class name to a feature title but
/// an explicit <c>[BobcatFeature("…")]</c> title has no way back, so the binding is recorded here
/// rather than re-derived.
/// </param>
/// <param name="TestMethod">The projected test's method name, or null in the Gherkin lane.</param>
public sealed record SpecManifestEntry(
    string Identity,
    string? TestClass = null,
    string? TestMethod = null)
{
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
