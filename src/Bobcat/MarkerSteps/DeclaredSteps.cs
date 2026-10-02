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
    /// </remarks>
    public static void Bind(string uid, string testClass, string testMethod)
        => _bindings[uid] = new SpecManifestEntry(uid, testClass, testMethod);

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
            _bindings.Values.OrderBy(entry => entry.Identity, SpecIdentity.Comparer).ToList());

    internal static void Clear()
    {
        _byUid.Clear();
        _bindings.Clear();
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
