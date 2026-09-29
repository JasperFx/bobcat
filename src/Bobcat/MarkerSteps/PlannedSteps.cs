using System.Collections.Concurrent;

namespace Bobcat;

/// <summary>
/// The grammar steps a projected test <b>will</b> call, in source order, keyed by the
/// <c>{Feature}/{Scenario}</c> identity they belong to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third kind of step, and why all three are kept apart.</b>
/// <see cref="DeclaredSteps"/> is what the author <i>said</i> in a marker comment.
/// <see cref="ScenarioRecorder.RecordedStep"/> is what actually <i>ran</i>. This is what the test
/// is <i>going to</i> call — every <c>[BobcatStep]</c> invocation in the method body, read off the
/// syntax tree at compile time.
/// </para>
/// <para>
/// <b>It buys two things that were impossible without it.</b> A projected specification can be
/// <b>previewed</b> — shown without being run, which is what <c>bobcat preview</c> does for a
/// <c>.feature</c> file and could not do here, because a projected test has no
/// <c>FeatureDefinition</c> and no fixture to build a plan from. And a scenario that <b>stopped
/// early</b> can render the steps it never reached in grey, instead of them silently vanishing —
/// Storyteller greyed them out, and the difference between "this step passed" and "this step was
/// never attempted" is the whole value of an aborted spec's report.
/// </para>
/// <para>
/// <b>Matched by ordinal, never by text.</b> Each call site has an index within its test method,
/// and the interceptor passes that index to the recorder — so "which planned steps ran" is a set
/// lookup rather than a guess. Matching on rendered text would break the moment a helper is called
/// twice, in a loop, or behind an <c>if</c>; matching on ordinal handles all three, because the
/// ordinal is a fact about the source rather than about the execution.
/// </para>
/// </remarks>
public static class PlannedSteps
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<PlannedStep>> _byUid = new();

    /// <summary>
    /// Declare the grammar steps of one scenario, in source order. Called from generated code;
    /// last registration wins.
    /// </summary>
    public static void Register(string uid, params PlannedStep[] steps) => _byUid[uid] = steps;

    /// <summary>The planned steps for a scenario, or an empty list — never null.</summary>
    public static IReadOnlyList<PlannedStep> For(string uid)
        => _byUid.TryGetValue(uid, out var steps) ? steps : [];

    /// <summary>Every scenario identity with a plan. What a preview enumerates.</summary>
    public static IReadOnlyCollection<string> KnownScenarios => _byUid.Keys.ToList();

    internal static void Clear() => _byUid.Clear();
}

/// <summary>
/// One <c>[BobcatStep]</c> call a projected test will make.
/// </summary>
/// <param name="Keyword">Given, When, Then — or empty, for a grammar that spells no keyword.</param>
/// <param name="Template">
/// The step text as the attribute declares it, placeholders and all — <c>Start with {value}</c>.
/// Deliberately NOT the rendered sentence: at preview time the arguments have not been evaluated,
/// and a preview that invented values would be describing a run that never happened.
/// </param>
/// <param name="Grammar">
/// The helper this step binds to, as <c>Type.Method</c>. The answer to "why did my step match that
/// grammar" — the same question <c>PreviewRender</c>'s bindings exist to answer for the Gherkin lane.
/// </param>
/// <param name="DeclaredStepNumber">
/// 1-based position in <see cref="DeclaredSteps"/> of the marker comment this call sits under, or
/// null when it sits under none.
/// </param>
/// <param name="Line">1-based line of the call site, so a preview can point at the source.</param>
public sealed record PlannedStep(
    string Keyword,
    string Template,
    string Grammar,
    int? DeclaredStepNumber,
    int Line)
{
    public override string ToString() => Keyword.Length > 0 ? $"{Keyword} {Template}" : Template;
}
