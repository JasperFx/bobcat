using System.Runtime.ExceptionServices;
using Bobcat.Engine;

namespace Bobcat;

/// <summary>
/// A run of assertions that all get a chance to be evaluated, throwing only at the next action.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem.</b> An assertion library throws, so a test's first disagreement is its last: four
/// assertions in a row report one failure and three blanks. Storyteller gathered every wrong in a
/// specification and kept going, and that is most of what makes a specification report worth reading.
/// <see cref="SpecAssert"/> gives that to a Bobcat grammar; this gives it to an ordinary
/// <c>x.ShouldBe(7)</c>, with the test author writing nothing.
/// </para>
/// <para>
/// <b>Why it flushes at the next ACTION rather than at the end of the test.</b> A gathered failure
/// means the system disagreed with the specification, so every later assertion about the same values
/// is still worth hearing — but the next <i>action</i> would be operating on state the assertions just
/// proved wrong, and whatever it reported after that would be noise. So a maximal run of consecutive
/// assertion statements is evaluated together and the run's failures are thrown at its end, which the
/// generator marks as the last call site of the run.
/// </para>
/// <para>
/// <b>Only a statement-level call is ever gathered.</b> That is the generator's rule, and it is what
/// makes chaining safe: <c>x.ShouldNotBeNull().Name.ShouldBe("a")</c> consumes the first assertion's
/// result, so swallowing it would dereference null and report a <c>NullReferenceException</c> instead
/// of the assertion that actually failed. A call whose value is used throws as it always did.
/// </para>
/// </remarks>
public static class AssertionRun
{
    /// <summary>
    /// Run one assertion inside <paramref name="step"/>, gathering an assertion failure rather than
    /// letting it end the test — and, when <paramref name="flush"/>, throwing the run's failures.
    /// </summary>
    public static void Gather(Action assertion, IDisposable step, bool flush)
    {
        // With no scenario open nobody is collecting, and swallowing here would turn a red test green.
        // A decorated helper — or an assertion in a test that never opened a scenario — behaves exactly
        // as it did before.
        if (ScenarioRecorder.Current is not { } recording)
        {
            using (step) assertion();
            return;
        }

        try
        {
            assertion();
        }
        catch (Exception e) when (ProjectedFailure.IsAssertion(e))
        {
            (step as IStepHandle)?.Fail(e);
            recording.GatherAssertionFailure(e);
        }
        catch (Exception e)
        {
            // Not an assertion: the code broke rather than disagreed, and there is nothing to be
            // learned from carrying on. The step takes the blame and the exception goes where it was
            // going.
            (step as IStepHandle)?.Fail(e);
            step.Dispose();
            throw;
        }

        step.Dispose();

        if (flush) recording.FlushAssertionRun();
    }

    /// <summary>
    /// The same, with the CELL built from the call site rather than parsed out of a message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the cell has to be built here.</b> Shouldly recovers its subject expression by reading the
    /// source at the call site — which is exactly what interception moves. Intercepted, it finds a line
    /// of generated code and falls back to printing the actual value as the subject and "but was not"
    /// with nothing after it: a message strictly worse than the one it used to produce.
    /// </para>
    /// <para>
    /// So nothing is parsed. The <paramref name="subject"/> is the receiver expression the generator read
    /// from the syntax tree, and the values are the ones the interceptor was handed — which is better
    /// than the message ever was, because it is data rather than prose, and it is available whether the
    /// assertion reads well or not.
    /// </para>
    /// </remarks>
    /// <param name="subject">The receiver expression as written — the cell's name.</param>
    /// <param name="actual">The receiver's value.</param>
    /// <param name="expected">The expectation, when the assertion has exactly one.</param>
    /// <param name="comparison">
    /// What the assertion actually compared (issue #384). The generator only reaches this overload
    /// when its dialect had a member for the assertion, so there is no "unknown" to represent: an
    /// assertion outside the closed set goes through the cell-less overload instead and renders as
    /// a plain step line.
    /// </param>
    public static void Gather(
        Action assertion, IDisposable step, bool flush,
        string subject, object? actual, object? expected,
        Comparison comparison = Comparison.Equals)
    {
        if (ScenarioRecorder.Current is not { } recording)
        {
            using (step) assertion();
            return;
        }

        try
        {
            assertion();
        }
        catch (Exception e) when (ProjectedFailure.IsAssertion(e))
        {
            if (step is IStepHandle handle)
            {
                handle.AddCell(cellFor(subject, actual, expected, comparison));
                handle.Fail(e);
            }

            recording.GatherAssertionFailure(e);
        }
        catch (Exception e)
        {
            (step as IStepHandle)?.Fail(e);
            step.Dispose();
            throw;
        }

        step.Dispose();

        if (flush) recording.FlushAssertionRun();
    }

    private static CellResult cellFor(
        string subject, object? actual, object? expected, Comparison comparison)
        => new(subject, ResultStatus.failed)
        {
            // Null when the assertion has no single expectation — ShouldNotBeNull(),
            // ShouldBeEmpty(). The cell then reports only what the value WAS, which is all there
            // is to say, and the comparison below is what says why that is enough.
            Expected = StepText.Value(expected),
            Actual = StepText.Value(actual),

            // Without this the cell claimed an equality it never checked: ShouldBeGreaterThan(10)
            // against 3 rendered "expected '10', got '3'", and 10 is the bound (issue #384).
            Comparison = comparison
        };

    /// <summary>The same for an assertion that returns a value the caller discards.</summary>
    public static T? Gather<T>(Func<T> assertion, IDisposable step, bool flush)
    {
        var result = default(T);
        Gather(() => result = assertion(), step, flush);
        return result;
    }

    /// <summary>
    /// Throw what a run gathered: the original exception when there was one — so an IDE still shows
    /// the library's own expected/actual diff — and a summary when there were several.
    /// </summary>
    internal static void Throw(List<Exception> gathered)
    {
        if (gathered.Count == 0) return;

        if (gathered.Count == 1)
        {
            // Rethrown with its stack intact, so the failure a developer sees at the breakpoint is the
            // one their assertion library produced and not a Bobcat wrapper around it.
            ExceptionDispatchInfo.Capture(gathered[0]).Throw();
        }

        var lines = gathered.Select((x, i) => $"  {i + 1}. {x.Message.Replace("\n", "\n     ")}");

        throw new SpecAssertionException(
            $"{gathered.Count} assertions failed before the next action:" + Environment.NewLine
            + string.Join(Environment.NewLine, lines));
    }
}
