using System.Collections.Concurrent;
using Bobcat.Runtime;

namespace Bobcat;

/// <summary>
/// The steps a test <b>declares</b> through marker comments (issue #110), keyed by the
/// <c>{Feature}/{Scenario}</c> identity they belong to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a registry and not a lookup at the point of use.</b> Comments are erased by the
/// compiler, so this table is the only surviving record of them. The generator emits one
/// registration per assembly and a module initializer runs it, which is what lets a test opt in by
/// writing comments and nothing else — no base class, no attribute on every method, no call the
/// author has to remember.
/// </para>
/// <para>
/// <b>Declared is not executed.</b> Everything here is known at compile time: what the test says
/// it does, in order. A step that actually ran, with a duration, comes from
/// <see cref="ScenarioRecorder.RecordedStep"/>. Keeping the two apart is the whole point — the
/// narrative is trustworthy because it was never inferred from what happened.
/// </para>
/// </remarks>
public static class DeclaredSteps
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<DeclaredStep>> _byUid = new();

    private static readonly ConcurrentDictionary<string, SpecManifestEntry> _bindings = new();

    /// <summary>
    /// Each bound test's Event Model coordinates (bobcat#573), keyed by test class and method. Keyed by
    /// the test rather than by identity because that is what a runner adapter has in hand at
    /// discovery, and first-wins like the bindings.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SpecCoordinates> _coordinates = new();

    /// <summary>
    /// Every test that tried to bind an identity another test already holds (bobcat#489), keyed by
    /// identity and then by test, so a binding repeated under contention or by a reloaded assembly
    /// is recorded once.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SpecManifestEntry>> _duplicates = new();

    /// <summary>
    /// Declare the ordered steps of one scenario. Called from generated code; last registration
    /// wins, so a rebuilt assembly loaded twice in one process does not accumulate.
    /// </summary>
    public static void Register(string uid, params DeclaredStep[] steps) => _byUid[uid] = steps;

    /// <summary>The declared steps for a scenario, or an empty list — never null.</summary>
    public static IReadOnlyList<DeclaredStep> For(string uid)
        => _byUid.TryGetValue(uid, out var steps) ? steps : [];

    /// <summary>Every registered identity. Exists so a runner can report what it expected to find.</summary>
    public static IReadOnlyCollection<string> KnownScenarios => _byUid.Keys.ToList();

    /// <summary>
    /// Bind an identity to the test method it was derived from (issue #391). Called from generated
    /// code, beside <see cref="Register"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the binding is recorded rather than re-derived.</b> The identity is a one-way
    /// function of the class and method names: <see cref="MarkerSpecNaming"/> reads
    /// <c>a_proposal_is_confirmed</c> as a sentence and strips a <c>Specs</c> suffix, and an
    /// explicit <c>[BobcatFeature("Booking appointments")]</c> title has no relationship to its
    /// class name at all. So "run this specification" cannot be answered by inverting the string —
    /// it needs the pair the generator saw, which is exactly what this is.
    /// </para>
    /// <para>
    /// It is here and not in a registry of its own because the two facts have one source and one
    /// lifetime: the generated module initializer declares a scenario's steps and its method in
    /// the same breath, and a scenario with no marker comments is not registered either way.
    /// </para>
    /// <para>
    /// <b>A second test claiming a bound identity is recorded, not overwritten</b> (bobcat#489). It
    /// used to be last-one-wins, so the listing silently named whichever test bound last and the
    /// other vanished from it. The first binding stays, and both are reported as
    /// <see cref="SpecManifest.Duplicates"/> -- the same test binding again (an assembly loaded twice)
    /// is not a duplicate.
    /// </para>
    /// </remarks>
    public static void Bind(string uid, string testClass, string testMethod)
        => Bind(uid, testClass, testMethod, null);

    /// <summary>
    /// Bind one constant row of a data-driven specification (bobcat#569): its own identity, the test
    /// method every row shares, and the method's identity as its <paramref name="parent"/>. Called from
    /// generated code. Rows only a run produces (<c>[MemberData]</c>) are never bound, because nothing
    /// knows them before the run.
    /// </summary>
    public static void Bind(string uid, string testClass, string testMethod, string? parent)
        => Bind(uid, testClass, testMethod, parent, null, null, null, null);

    /// <summary>
    /// Bind an identity — a method's, or one of its rows' with <paramref name="parent"/> — to its test
    /// method together with its Event Model coordinates (bobcat#573): the slice, chapter, domain and
    /// pattern the generator folded for it. Called from generated code. A row carries its method's
    /// coordinates, since it is the same test.
    /// </summary>
    /// <remarks>
    /// The coordinates are recorded beside the binding, and the runner adapters read them back by test
    /// method (<see cref="CoordinatesFor(string, string)"/>) to project them as platform traits — so
    /// the manifest <c>bobcat run --slice</c> resolves through and the traits
    /// <c>dotnet test --filter-trait</c> sees are one record, not two derivations.
    /// </remarks>
    public static void Bind(string uid, string testClass, string testMethod, string? parent,
        string? slice, string? chapter, string? domain, string? pattern)
    {
        var entry = new SpecManifestEntry(uid, testClass, testMethod, parent, slice, chapter, domain, pattern);

        if (SpecCoordinates.Of(entry) is { } coordinates)
        {
            _coordinates.TryAdd(keyOf(entry), coordinates);
        }

        _bindings.AddOrUpdate(uid, entry, (_, existing) =>
        {
            if (sameTest(existing, entry)) return entry;

            var claims = _duplicates.GetOrAdd(uid, _ => new ConcurrentDictionary<string, SpecManifestEntry>());
            claims.TryAdd(keyOf(existing), existing);
            claims.TryAdd(keyOf(entry), entry);
            return existing;
        });
    }

    /// <summary>
    /// Every identity two or more tests bound in this process (bobcat#489), each with the tests that
    /// claim it, sorted. Empty when every identity names one test.
    /// </summary>
    /// <remarks>
    /// BOBCAT036 refuses this at build time for the tests one compilation can see together. What
    /// reaches here is what it could not: specs in two assemblies loaded into one process, or the
    /// error suppressed.
    /// </remarks>
    public static IReadOnlyList<SpecManifestDuplicate> Duplicates
        => _duplicates
            .Select(pair => new SpecManifestDuplicate(
                pair.Key,
                pair.Value.Values.OrderBy(x => x.QualifiedTestMethod, StringComparer.Ordinal).ToList()))
            .OrderBy(x => x.Identity, SpecIdentity.Comparer)
            .ToList();

    private static bool sameTest(SpecManifestEntry left, SpecManifestEntry right)
        => string.Equals(left.TestClass, right.TestClass, StringComparison.Ordinal)
           && string.Equals(left.TestMethod, right.TestMethod, StringComparison.Ordinal);

    private static string keyOf(SpecManifestEntry entry) => entry.TestClass + "\n" + entry.TestMethod;

    /// <summary>
    /// The Event Model coordinates the generator bound for a test method (bobcat#573), or null when it
    /// bound none. <paramref name="testClass"/> is the full name with nested classes joined by
    /// <c>+</c>, as <see cref="Type.FullName"/> spells it.
    /// </summary>
    public static SpecCoordinates? CoordinatesFor(string testClass, string testMethod)
        => _coordinates.TryGetValue(testClass + "\n" + testMethod, out var coordinates) ? coordinates : null;

    /// <summary>The same for a test's declaring type — what a runner adapter has in hand.</summary>
    /// <remarks>
    /// Runs <paramref name="testClass"/>'s module initializer first, which is where the bindings are
    /// registered: a test framework may reach a test's attributes before anything else in that
    /// assembly has run. Running it twice is a no-op.
    /// </remarks>
    public static SpecCoordinates? CoordinatesFor(Type? testClass, string? testMethod)
    {
        if (testClass?.FullName is not { } name || string.IsNullOrEmpty(testMethod)) return null;

        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(testClass.Module.ModuleHandle);
        }
        catch
        {
            // A module that cannot run its initializer has registered nothing; there is simply no answer
        }

        return CoordinatesFor(name, testMethod);
    }

    private static readonly ConcurrentDictionary<string, string> _inherited = new();

    /// <summary>
    /// Record that the specification <paramref name="uid"/> runs a test body a base class declares,
    /// whose own identity is <paramref name="inheritedFrom"/> (bobcat#600). Called from generated code
    /// in the assembly of the concrete test class, beside its <see cref="Bind(string, string, string)"/>.
    /// </summary>
    /// <remarks>
    /// The body's marker comments and planned steps are registered where the body is written — under
    /// the base's identity, possibly in another assembly — so this is how a preview of the subclass's
    /// specification finds them. At run time the recording works the same out from reflection.
    /// </remarks>
    public static void Inherit(string uid, string inheritedFrom) => _inherited[uid] = inheritedFrom;

    /// <summary>The base-class identity a specification inherits its body from, or null (bobcat#600).</summary>
    public static string? InheritedFrom(string uid) => _inherited.TryGetValue(uid, out var from) ? from : null;

    /// <summary>Every specification that inherits its body from a base class (bobcat#600).</summary>
    public static IReadOnlyCollection<string> InheritedScenarios => _inherited.Keys.ToList();

    /// <summary>The test method an identity was derived from, or null when nothing bound it.</summary>
    public static SpecManifestEntry? BindingFor(string uid)
        => _bindings.TryGetValue(uid, out var binding) ? binding : null;

    /// <summary>
    /// What this assembly's projected tests specify, as the document a listing request writes
    /// (issue #391) — every bound identity, with the method a filter can ask for.
    /// </summary>
    /// <remarks>
    /// Built from the bindings rather than from <see cref="KnownScenarios"/>, so an identity whose
    /// steps registered but whose method did not cannot reach a manifest as an entry nothing can
    /// run. In practice they are registered together and the two sets are equal.
    /// </remarks>
    public static SpecManifest Manifest(string framework, string suite)
        => new(
            SpecManifest.ProjectedLane,
            framework,
            suite,
            _bindings.Values.OrderBy(entry => entry.Identity, SpecIdentity.Comparer).ToList())
        {
            Duplicates = Duplicates is { Count: > 0 } duplicates ? duplicates : null
        };

    /// <summary>
    /// The manifest, plus the recovery hints <paramref name="assembly"/> declares on itself
    /// (issue #469). What the generated module initializer calls, passing its own assembly.
    /// </summary>
    /// <remarks>
    /// The hints are read here, in the suite's process, because this is the only side of the
    /// boundary that has the attributes: a supervisor never loads the test assembly. Read lazily —
    /// only when a listing was asked for — so a suite pays for the reflection only then.
    /// </remarks>
    public static SpecManifest Manifest(string framework, string suite, System.Reflection.Assembly assembly)
    {
        var manifest = Manifest(framework, suite);
        return manifest with
        {
            Specs = manifest.Specs.Select(entry => SpecTraits.WithDeclaredTraits(entry, assembly)).ToList(),
            RecoveryHints = SpecManifestHint.ReadFrom(assembly) is { Count: > 0 } hints ? hints : null
        };
    }

    /// <summary>
    /// Forget one scenario's declared steps and binding. Test-only, and deliberately per scenario: this registry is
    /// process-wide, test classes run in parallel, and a wholesale clear from one class wiped the
    /// registrations another class had just made — the intermittent empty renders in the
    /// <c>MarkerSteps</c> tests. A test registers under a uid of its own and removes only that.
    /// </summary>
    internal static void Remove(string uid)
    {
        _byUid.TryRemove(uid, out _);
        if (_bindings.TryRemove(uid, out var binding)) _coordinates.TryRemove(keyOf(binding), out _);
        _duplicates.TryRemove(uid, out _);
        _inherited.TryRemove(uid, out _);
    }
}

/// <summary>
/// One step read from a marker comment: <c>// Given a proposed appointment</c>.
/// </summary>
/// <param name="Keyword">Given, When, Then, And or But — as written.</param>
/// <param name="Text">The sentence after the keyword.</param>
/// <param name="Line">
/// 1-based line of the comment in its source file. Carried because attributing a failure to the
/// step it fell inside needs it, and this is the only place the information exists.
/// </param>
public sealed record DeclaredStep(string Keyword, string Text, int Line)
{
    public override string ToString() => Keyword.Length > 0 ? $"{Keyword} {Text}" : Text;
}
