namespace Bobcat;

/// <summary>
/// The step that references a named arrangement (issue #259): <c>Given the arrangement "…"</c>.
/// It exists for the editors, not for the runner.
/// </summary>
/// <remarks>
/// <para>
/// An <c>@arrangement</c> scenario is a named list of Given steps, and the generator replaces every
/// <c>the arrangement "…"</c> step with that list at compile time, before step matching. So this
/// method is never bound and never called — it is not on any fixture, and the generator refuses a
/// reference naming no arrangement (BOBCAT021) rather than letting it reach the runner.
/// </para>
/// <para>
/// What it buys is a <b>real step definition</b>. VS Code's Cucumber extension finds steps by a
/// tree-sitter query over <c>[Given]</c>/<c>[When]</c>/<c>[Then]</c> attributes with a string
/// literal argument, in source. With this declared, an arrangement reference gets completion and
/// go-to-definition like any other step instead of an "undefined step" underline. That is also why
/// the expression is written as a literal rather than as <c>Arrangements.ReferenceExpression</c>:
/// the query cannot see a constant. The two are pinned together by a test in
/// <c>Bobcat.Generators.Tests</c>. This file ships as source in the Bobcat package for the same
/// reason — see <c>docs/editor-integration.md</c>.
/// </para>
/// </remarks>
public static class ArrangementSteps
{
    /// <summary>
    /// Inlines the named <c>@arrangement</c> scenario from the same feature file. Replaced at
    /// compile time; throws if it is ever invoked, because that would mean the replacement did not
    /// happen and the scenario is running without the history it says it has.
    /// </summary>
    [Given("the arrangement {string}")]
    public static void TheArrangement(string name)
        => throw new InvalidOperationException(
            $"'Given the arrangement \"{name}\"' ran as a step. The Bobcat generator replaces an arrangement "
            + "reference with the @arrangement scenario's own steps at compile time, so this should be "
            + "impossible — the scenario would otherwise run without the history it claims to arrange.");
}
