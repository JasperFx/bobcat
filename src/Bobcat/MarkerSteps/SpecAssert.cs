using Bobcat.Engine;
using Bobcat.Engine.Verification;

namespace Bobcat;

/// <summary>
/// What a projected test's step helper calls to report a <b>value comparison</b> or a plain
/// <b>wrong</b> — Storyteller's assertion sentences and facts, in a lane where the spec is an
/// ordinary xUnit or TUnit test.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not just an assertion library call.</b> Shouldly and <c>Assert.Equal</c> throw,
/// and a throw ends the test method — so a projected spec could only ever show its FIRST
/// disagreement, with every later step unrun. Storyteller gathered every wrong in a spec and kept
/// going, which is what makes a spec report worth reading: you see all four wrong cells, not the
/// first one. These methods record the disagreement on the step in progress and <b>return</b>,
/// leaving the scenario to carry on; the runner's verdict is settled once, at the end of the test,
/// by <see cref="MarkerStepRun.EndScenario"/>.
/// </para>
/// <para>
/// <b>The comparison is Bobcat's own, not a second opinion.</b> Every check goes through
/// <see cref="CellCheck"/> — the same type-aware comparison, the same <c>NULL</c>/<c>EMPTY</c>
/// tokens, the same <c>[Approx]</c> tolerance handling and the same structured
/// Expected/Actual/Note cell — that the Gherkin lane's return-value verification uses. A projected
/// spec and a <c>.feature</c> spec checking the same value therefore agree, and render identically.
/// </para>
/// <para>
/// <b>Silent outside a scenario.</b> Like every other marker-step call, a helper is used from
/// plenty of places that are not specifications. With no scenario open these record nothing and
/// still return the comparison's answer, so a helper that branches on it behaves the same either
/// way.
/// </para>
/// </remarks>
public static class SpecAssert
{
    /// <summary>
    /// Compare <paramref name="actual"/> against <paramref name="expected"/> and record the result
    /// as a named cell on the step in progress. Returns whether they matched.
    /// </summary>
    /// <param name="name">
    /// The cell's name — the return-value alias in Storyteller terms. Use the word the step's
    /// sentence uses ("value", "sum", "product"), because that is what the report shows beside the
    /// expected/actual pair.
    /// </param>
    public static bool Check<T>(string name, T actual, T expected, CheckOptions? options = null)
        => record(CellCheck.For(name, actual, CheckFormat.Of(expected), options));

    /// <summary>
    /// The text-expected form: compare against the expected value <b>as a specification would
    /// write it</b>, so <c>NULL</c>, <c>EMPTY</c> and a quoted literal all mean what they mean in a
    /// Gherkin cell.
    /// </summary>
    public static bool Check(string name, object? actual, string expected, CheckOptions? options = null)
        => record(CellCheck.ForValue(name, actual, expected, options));

    /// <summary>
    /// Storyteller's Fact: a single boolean condition, rendered as the step passing or failing.
    /// Returns the condition, so a helper can both report and answer.
    /// </summary>
    /// <param name="condition">What the fact claims.</param>
    /// <param name="because">
    /// Context for the failure, shown instead of a stack trace. A fact that fails without one says
    /// only that it failed, which is exactly the report Storyteller's <c>StoryTellerAssert</c>
    /// existed to improve on.
    /// </param>
    public static bool Fact(bool condition, string? because = null)
    {
        // No default message. A bare fact has nothing to say beyond "not true", and the red line
        // already says that — inventing a sentence for it is exactly the noise Storyteller's own
        // StoryTellerAssert existed to replace.
        if (!condition) Fail(because ?? "");
        return condition;
    }

    /// <summary>
    /// Mark the step in progress as a wrong, with a message and no stack trace — the direct
    /// counterpart of Storyteller's <c>StoryTellerAssert.Fail(message)</c>.
    /// </summary>
    /// <remarks>
    /// Recorded as a <see cref="SpecAssertionException"/> that is never thrown, which is the whole
    /// trick: the step carries the exact failure vocabulary a Gherkin step would, the report shows
    /// the message rather than a stack, and the scenario continues to its next step.
    /// </remarks>
    public static void Fail(string? message)
    {
        message ??= "";

        if (ScenarioRecorder.CurrentStep is { } step && step.Failure is null)
        {
            step.Failure = new SpecAssertionException(message);
        }
    }

    /// <summary>
    /// Fail only when <paramref name="condition"/> is true — Storyteller's
    /// <c>StoryTellerAssert.Fail(bool, message)</c>, kept with its original polarity so a suite
    /// being ported reads the same.
    /// </summary>
    public static void Fail(bool condition, string message)
    {
        if (condition) Fail(message);
    }

    /// <summary>
    /// Run <paramref name="assertion"/> and, if it throws an <b>assertion</b>, record it as a wrong
    /// on the step in progress and carry on — so a specification asserting with Shouldly, xUnit or
    /// NUnit can still show every disagreement it reached rather than only its first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The boundary is <see cref="ProjectedFailure.IsAssertion"/>: an assertion is gathered, and
    /// anything else is <b>rethrown unchanged</b>. Swallowing a <c>NullReferenceException</c> would
    /// turn a broken test into a merely red one, and the two need to stay distinguishable — an
    /// assertion disagreeing is a fact about the system, an exception is a fact about the code.
    /// </para>
    /// <para>
    /// Returns whether the assertion held, so a helper can branch on it.
    /// </para>
    /// </remarks>
    public static bool Gather(Action assertion)
    {
        try
        {
            assertion();
            return true;
        }
        catch (Exception e) when (ProjectedFailure.IsAssertion(e))
        {
            Fail(e.Message);
            return false;
        }
    }

    private static bool record(CellResult cell)
    {
        ScenarioRecorder.CurrentStep?.Cells.Add(cell);
        return cell.Status is ResultStatus.success or ResultStatus.ok;
    }
}
