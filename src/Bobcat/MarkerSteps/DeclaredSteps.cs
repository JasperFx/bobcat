using System.Collections.Concurrent;

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

    internal static void Clear() => _byUid.Clear();
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
