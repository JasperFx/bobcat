using Bobcat;
using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// A run of assertions all get evaluated, and the run's failures are thrown at its end rather than at
/// the first one.
/// </summary>
public class AssertionRunTests
{
    private static ScenarioRecorder.Recording begin()
        => ScenarioRecorder.Begin("Feature", "Scenario", null, Guid.NewGuid());

    private static void gather(string text, Action assertion, bool flush)
        => AssertionRun.Gather(assertion, ScenarioRecorder.Step("Then", text), flush,
            subject: text, actual: null, expected: null);

    [Fact]
    public void every_assertion_in_a_run_runs_and_the_last_one_throws()
    {
        using var recording = begin();
        var evaluated = 0;

        var thrown = Should.Throw<SpecAssertionException>(() =>
        {
            gather("first", () => { evaluated++; throw new ShouldAssertException("first wrong"); }, flush: false);
            gather("second", () => evaluated++, flush: false);
            gather("third", () => { evaluated++; throw new ShouldAssertException("third wrong"); }, flush: true);
        });

        // All three, not one. A plain assertion library would have stopped at the first.
        evaluated.ShouldBe(3);

        thrown.Message.ShouldContain("2 assertions failed before the next action");
        thrown.Message.ShouldContain("first wrong");
        thrown.Message.ShouldContain("third wrong");

        recording.Steps.Select(x => x.Status)
            .ShouldBe([ResultStatus.failed, ResultStatus.success, ResultStatus.failed]);
    }

    [Fact]
    public void a_single_failure_is_rethrown_as_itself()
    {
        using var recording = begin();

        // Rethrown rather than wrapped, so the failure a developer sees at the breakpoint is the one
        // their assertion library produced — and an IDE still shows its own expected/actual diff.
        Should.Throw<ShouldAssertException>(() =>
            gather("only", () => throw new ShouldAssertException("the one wrong"), flush: true))
            .Message.ShouldBe("the one wrong");
    }

    [Fact]
    public void a_run_with_nothing_wrong_throws_nothing()
    {
        using var recording = begin();

        Should.NotThrow(() =>
        {
            gather("first", () => { }, flush: false);
            gather("second", () => { }, flush: true);
        });

        recording.Steps.ShouldAllBe(x => x.Status == ResultStatus.success);
    }

    [Fact]
    public void a_failure_that_is_not_an_assertion_stops_the_run_immediately()
    {
        using var recording = begin();
        var reached = false;

        Should.Throw<InvalidOperationException>(() =>
        {
            gather("broken", () => throw new InvalidOperationException("boom"), flush: false);
            reached = true;
        });

        // The code broke rather than disagreed, and there is nothing to be learned from carrying on.
        reached.ShouldBeFalse();
        recording.Steps.Single().Status.ShouldBe(ResultStatus.error);
    }

    [Fact]
    public void outside_a_scenario_an_assertion_throws_exactly_as_it_always_did()
    {
        // With nobody collecting, swallowing would turn a red test green. This is the case a decorated
        // helper hits when it is called from somewhere that is not a specification.
        ScenarioRecorder.Current.ShouldBeNull();

        Should.Throw<ShouldAssertException>(() =>
            gather("nowhere", () => throw new ShouldAssertException("still throws"), flush: false));
    }

    [Fact]
    public void the_cell_is_built_from_the_call_site_rather_than_parsed_from_the_message()
    {
        using var recording = begin();

        Should.Throw<ShouldAssertException>(() => AssertionRun.Gather(
            () => throw new ShouldAssertException("a message intercepting has degraded"),
            ScenarioRecorder.Step("Then", "calculator.Value should be 7"),
            flush: true,
            subject: "calculator.Value", actual: 6, expected: 7));

        // Interception moves the call away from the source Shouldly reads its subject expression out of,
        // so the message gets WORSE. The generator already knows the subject and the interceptor already
        // holds the values — data, not prose.
        var cell = recording.Steps.Single().Cells.ShouldHaveSingleItem();
        cell.Name.ShouldBe("calculator.Value");
        cell.Expected.ShouldBe("7");
        cell.Actual.ShouldBe("6");
    }

    [Fact]
    public void the_cell_states_the_comparison_the_assertion_actually_made()
    {
        using var recording = begin();

        Should.Throw<ShouldAssertException>(() => AssertionRun.Gather(
            () => throw new ShouldAssertException("3 should be greater than 10"),
            ScenarioRecorder.Step("Then", "calculator.Value should be greater than 10"),
            flush: true,
            subject: "calculator.Value", actual: 3, expected: 10,
            comparison: Comparison.GreaterThan));

        // Issue #384. The cell used to render "expected '10', got '3'", which is a claim about
        // equality and false here — 10 is the bound. The sentence above it was right, because the
        // dialect writes the comparison into the step text, so the console read correctly while
        // the cell on its own did not.
        var cell = recording.Steps.Single().Cells.ShouldHaveSingleItem();

        cell.Comparison.ShouldBe(Comparison.GreaterThan);
        cell.DisplayText.ShouldBe("should be greater than '10', got '3'");
    }

    [Fact]
    public void an_assertion_outside_the_closed_set_produces_no_cell_at_all()
    {
        using var recording = begin();

        // The cell-less overload, which is the ONE the generator emits when its dialect has no
        // member for the assertion — ShouldBeTrue, ShouldBeEquivalentTo, ShouldBeOfType. The
        // choice is made at compile time by picking the overload, so the runtime never has to
        // decide whether a cell it was handed can be described.
        Should.Throw<ShouldAssertException>(() => AssertionRun.Gather(
            () => throw new ShouldAssertException("should be true but was false"),
            ScenarioRecorder.Step("Then", "the flag should be true"),
            flush: true));

        var step = recording.Steps.Single();

        step.Cells.ShouldBeEmpty(
            "a cell would have to state a comparison Bobcat cannot describe, so the honest "
            + "degradation is a plain step line");

        // It is still a step, with a verdict. Withholding the cell is not withholding the failure.
        step.Failure.ShouldNotBeNull();
    }

    [Fact]
    public void the_comparison_defaults_to_equality_so_an_older_caller_is_unchanged()
    {
        using var recording = begin();

        Should.Throw<ShouldAssertException>(() => AssertionRun.Gather(
            () => throw new ShouldAssertException("7 should be 6"),
            ScenarioRecorder.Step("Then", "calculator.Value should be 7"),
            flush: true,
            subject: "calculator.Value", actual: 6, expected: 7));

        var cell = recording.Steps.Single().Cells.ShouldHaveSingleItem();

        cell.Comparison.ShouldBe(Comparison.Equals);
        cell.DisplayText.ShouldBe("expected '7', got '6'");
    }

    /// <summary>Stands in for Shouldly's, which core cannot reference.</summary>
    private sealed class ShouldAssertException(string message) : Exception(message);
}
