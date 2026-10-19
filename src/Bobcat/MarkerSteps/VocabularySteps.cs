using System.Collections.Concurrent;

namespace Bobcat;

/// <summary>
/// The scenario vocabulary calls a projected test makes — <c>GivenEvents</c>, <c>WhenReceived</c>,
/// <c>ThenDocument</c> and the rest of a vocabulary such as WolverineFx.Bobcat's — in source
/// order, keyed by the <c>{Feature}/{Scenario}</c> identity they belong to (bobcat#625).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read by the preview, and by nothing else.</b> A vocabulary step writes its sentence from its
/// arguments when it runs, so there is no template to plan and no ordinal the recorder could report it
/// reached by. Kept apart from <see cref="PlannedSteps"/> for exactly that reason: a planned step that is
/// never reported as reached renders grey as "never attempted", and every vocabulary step would.
/// </para>
/// <para>
/// Without it a suite written in the vocabulary — the way the skills recommend — previewed nearly
/// empty: only its own <c>[Given]</c>/<c>[When]</c>/<c>[Then]</c> helpers and marker comments showed.
/// </para>
/// </remarks>
public static class VocabularySteps
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<VocabularyStep>> _byUid = new();

    /// <summary>Declare one scenario's vocabulary calls. Called from generated code; last registration wins.</summary>
    public static void Register(string uid, params VocabularyStep[] steps) => _byUid[uid] = steps;

    /// <summary>The vocabulary calls for a scenario, or an empty list — never null.</summary>
    public static IReadOnlyList<VocabularyStep> For(string uid)
        => _byUid.TryGetValue(uid, out var steps) ? steps : [];

    /// <summary>Every scenario identity that makes a vocabulary call.</summary>
    public static IReadOnlyCollection<string> KnownScenarios => _byUid.Keys.ToList();

    /// <summary>Forget one scenario's calls. Test-only, per scenario, for the reason <see cref="PlannedSteps"/> gives.</summary>
    internal static void Remove(string uid) => _byUid.TryRemove(uid, out _);
}

/// <summary>One scenario vocabulary call a projected test will make.</summary>
/// <param name="Keyword">Given, When or Then — the one the method's name opens with.</param>
/// <param name="Text">
/// The rest of the method's name as words, then the types the call names (<c>received ShipOrder</c>).
/// Types, never values: at preview time no argument has been evaluated.
/// </param>
/// <param name="Grammar">The vocabulary method, as <c>Type.Method</c>.</param>
/// <param name="Line">1-based line of the call site.</param>
public sealed record VocabularyStep(string Keyword, string Text, string Grammar, int Line)
{
    public override string ToString() => Text.Length > 0 ? $"{Keyword} {Text}" : Keyword;
}
