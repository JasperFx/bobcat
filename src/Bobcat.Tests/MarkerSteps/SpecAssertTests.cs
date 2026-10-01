using Bobcat;
using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// A projected step reports value comparisons and gathered wrongs, so that a specification written
/// as an xUnit test shows every disagreement it reached rather than only its first.
/// </summary>
/// <remarks>
/// The Storyteller sample this exists for is <c>Specs/Assertions/Asserting_Values.md</c>, whose own
/// comment says it was written to fail on purpose: one right sentence, one wrong one, and two more
/// after it that still had to be judged. An assertion library throws, which would have ended the
/// test at the wrong one and left the report with a failure and two blanks.
/// </remarks>
public class SpecAssertTests
{
    private static ScenarioRecorder.Recording begin()
        => ScenarioRecorder.Begin("Feature", "Scenario", null, Guid.NewGuid());

    [Fact]
    public void a_matching_check_records_a_successful_cell()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the value should be 6"))
        {
            SpecAssert.Check("value", 6, 6).ShouldBeTrue();
        }

        var cell = recording.Steps.Single().Cells.Single();
        cell.Name.ShouldBe("value");
        cell.Status.ShouldBe(ResultStatus.success);
        recording.Steps.Single().Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void a_mismatched_check_records_expected_and_actual()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the value should be 7"))
        {
            SpecAssert.Check("value", 6, 7).ShouldBeFalse();
        }

        var cell = recording.Steps.Single().Cells.Single();
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Expected.ShouldBe("7");
        cell.Actual.ShouldBe("6");

        // The same wording the Gherkin lane's return-value verification produces, because it is the
        // same comparison — CellCheck — and not a second opinion about the same values.
        cell.DisplayText.ShouldBe("expected '7', got '6'");
    }

    [Fact]
    public void several_checks_in_one_step_are_judged_separately()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the sum and the product"))
        {
            SpecAssert.Check("Sum", 8, 6);
            SpecAssert.Check("Product", 16, 8);
        }

        // Storyteller's output-parameter sentence: two claims in one line, and a wrong product must
        // not be allowed to hide a right sum — nor the reverse.
        recording.Steps.Single().Cells.Select(x => x.Status)
            .ShouldBe([ResultStatus.failed, ResultStatus.failed]);
        recording.Counts.ShouldBe(new Counts(0, 2, 0));
    }

    [Fact]
    public void a_false_fact_is_a_wrong_and_the_scenario_keeps_going()
    {
        using var recording = begin();

        using (ScenarioRecorder.Step("Then", "this line is always false"))
        {
            SpecAssert.Fact(false).ShouldBeFalse();
        }

        using (ScenarioRecorder.Step("Then", "this line is always true"))
        {
            SpecAssert.Fact(true).ShouldBeTrue();
        }

        recording.Steps[0].Status.ShouldBe(ResultStatus.failed);
        recording.Steps[1].Status.ShouldBe(ResultStatus.success);
        recording.Counts.ShouldBe(new Counts(1, 1, 0));
    }

    [Fact]
    public void a_fact_failure_carries_its_reason_and_no_stack_trace()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the confirmation email was sent"))
        {
            SpecAssert.Fail(true, "The email server is not reachable");
        }

        var step = recording.Steps.Single();
        step.Status.ShouldBe(ResultStatus.failed);
        step.Failure.ShouldBeOfType<SpecAssertionException>().Message
            .ShouldBe("The email server is not reachable");

        // Never thrown, so there is no stack. That is the whole point of Storyteller's
        // StoryTellerAssert over an exception: the report shows the reason, not the plumbing.
        step.Failure!.StackTrace.ShouldBeNull();
    }

    [Fact]
    public void fail_keeps_storytellers_polarity()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "nothing is wrong"))
        {
            SpecAssert.Fail(false, "not reported");
        }

        recording.Steps.Single().Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void the_first_failure_on_a_step_is_the_one_kept()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "two reasons"))
        {
            SpecAssert.Fail("the first reason");
            SpecAssert.Fail("the second reason");
        }

        recording.Steps.Single().Failure!.Message.ShouldBe("the first reason");
    }

    [Fact]
    public void gathered_failures_describe_every_step_that_failed()
    {
        using var recording = begin();

        using (ScenarioRecorder.Step("Then", "the value should be 7")) SpecAssert.Check("value", 6, 7);
        using (ScenarioRecorder.Step("Then", "the fact holds")) SpecAssert.Fact(false, "it does not");
        using (ScenarioRecorder.Step("Then", "the value should be 6")) SpecAssert.Check("value", 6, 6);

        var gathered = recording.GatheredFailures().ShouldNotBeNull();
        gathered.ShouldContain("2 specification steps failed:");
        gathered.ShouldContain("value: expected '7', got '6'");
        gathered.ShouldContain("it does not");
        gathered.ShouldNotContain("the value should be 6");
    }

    [Fact]
    public void a_green_scenario_gathers_nothing()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the value should be 6")) SpecAssert.Check("value", 6, 6);

        recording.GatheredFailures().ShouldBeNull();
    }

    [Fact]
    public void a_check_outside_a_step_records_nothing_and_still_answers()
    {
        using var recording = begin();

        // A grammar helper gets called from setup code and from other helpers. Reporting from
        // outside a step would attach a cell to whatever step happened to be open next.
        SpecAssert.Check("value", 6, 6).ShouldBeTrue();
        SpecAssert.Check("value", 6, 7).ShouldBeFalse();

        recording.Steps.ShouldBeEmpty();
        recording.GatheredFailures().ShouldBeNull();
    }

    [Fact]
    public void a_check_outside_a_scenario_does_not_throw()
    {
        ScenarioRecorder.Current.ShouldBeNull();
        SpecAssert.Check("value", 6, 7).ShouldBeFalse();
        SpecAssert.Fact(false).ShouldBeFalse();
        Should.NotThrow(() => SpecAssert.Fail("nowhere to report this"));
    }

    [Fact]
    public void an_assertion_library_can_join_the_gathered_wrongs()
    {
        using var recording = begin();

        using (ScenarioRecorder.Step("Then", "the value should be 7"))
        {
            SpecAssert.Gather(() => 6.ShouldBe(7)).ShouldBeFalse();
        }

        using (ScenarioRecorder.Step("Then", "the value should be 6"))
        {
            SpecAssert.Gather(() => 6.ShouldBe(6)).ShouldBeTrue();
        }

        // The whole point: the second step RAN. A bare Shouldly call would have ended the test at
        // the first one and the specification would show one failure and one blank.
        recording.Steps.Select(x => x.Status).ShouldBe([ResultStatus.failed, ResultStatus.success]);
        recording.Steps[0].Failure!.Message.ShouldContain("should be");
    }

    [Fact]
    public void gather_rethrows_anything_that_is_not_an_assertion()
    {
        using var recording = begin();

        using (ScenarioRecorder.Step("Then", "the service answers"))
        {
            // Swallowing this would turn a broken test into a merely red one, and the two have to
            // stay distinguishable: an assertion disagreeing is a fact about the system under test,
            // an exception is a fact about the code.
            Should.Throw<InvalidOperationException>(
                () => SpecAssert.Gather(() => throw new InvalidOperationException("boom")));
        }
    }

    [Fact]
    public void the_expected_value_may_be_written_as_specification_text()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the note is empty"))
        {
            // The token vocabulary a Gherkin cell uses, so a projected check and a .feature check
            // mean the same thing by EMPTY.
            SpecAssert.Check("note", "", "EMPTY").ShouldBeTrue();
        }

        recording.Steps.Single().Cells.Single().Status.ShouldBe(ResultStatus.success);
    }
}
