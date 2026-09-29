using Bobcat;
using Bobcat.Engine;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// A projected test renders through the same <see cref="SpecRender"/> model a <c>.feature</c>
/// scenario does — so "Bobcat's specification output" means one thing rather than two.
/// </summary>
public class ProjectedSpecRenderTests
{
    private static ScenarioRecorder.Recording begin(string scenario = "Scenario")
        => ScenarioRecorder.Begin("Feature", scenario, null, Guid.NewGuid());

    [Fact]
    public void a_recorded_step_carries_its_keyword_as_written()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Given", "a")) { }
        using (ScenarioRecorder.Step("Given", "b")) { }

        var render = SpecRender.FromRecording(recording);

        // `And`, which StepKind has no member for — which is exactly why Keyword is carried.
        render.Steps.Select(x => x.Keyword).ShouldBe(["Given", "And"]);
        render.Steps.Select(x => x.Kind).ShouldBe([StepKind.Given, StepKind.Then]);
    }

    [Fact]
    public void a_step_called_from_another_step_nests_under_it()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Given", "invoice INV-2 is open"))
        using (ScenarioRecorder.Step("Given", "invoice INV-2 is open true and due on Friday"))
        {
        }

        var render = SpecRender.FromRecording(recording);

        // Storyteller's curried grammar: one authored sentence whose body is another grammar.
        // Flattened, the report says the same thing twice.
        render.Steps.Select(x => x.Depth).ShouldBe([0, 1]);
    }

    [Fact]
    public void an_outer_step_fails_when_its_inner_step_failed()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Given", "the curried sentence"))
        using (ScenarioRecorder.Step("Given", "the sentence it delegates to"))
        {
            SpecAssert.Fail("the inner step disagreed");
        }

        SpecRender.FromRecording(recording).Steps.Select(x => x.Status)
            .ShouldBe([ResultStatus.failed, ResultStatus.failed]);
    }

    [Fact]
    public void a_scenario_with_no_recorded_steps_renders_the_comments_it_declared()
    {
        var uid = "Feature/narrated";
        DeclaredSteps.Register(uid,
            new DeclaredStep("Given", "a calculator starting with 3", 10),
            new DeclaredStep("When", "it is multiplied by 2", 13),
            new DeclaredStep("Then", "the value should be 6", 16));

        using var recording = begin("narrated");
        var render = SpecRender.FromRecording(recording);

        render.Steps.Select(x => x.StepText)
            .ShouldBe(["a calculator starting with 3", "it is multiplied by 2", "the value should be 6"]);

        // `ok`, not `success`. A comment is prose: it makes no claim, so it can neither pass nor
        // fail on its own account, and painting it green because the test passed starts the
        // narrative lying.
        render.Steps.Select(x => x.Status).ShouldAllBe(x => x == ResultStatus.ok);
        render.Steps.ShouldAllBe(x => x.IsNarrative);
    }

    [Fact]
    public void recorded_steps_nest_under_the_comment_they_ran_inside()
    {
        var uid = "Feature/mixed";
        DeclaredSteps.Register(uid,
            new DeclaredStep("Given", "a doubled calculator", 10),
            new DeclaredStep("Then", "the arithmetic holds", 14));

        using var recording = begin("mixed");

        using (ScenarioRecorder.Step("Given", "start with 3", 0)) { }
        using (ScenarioRecorder.Step("When", "multiply by 2", 0)) { }
        using (ScenarioRecorder.Step("Then", "the value should be 7", 1))
        {
            SpecAssert.Check("value", 6, 7);
        }

        var render = SpecRender.FromRecording(recording);

        render.Steps.Select(x => (x.StepText, x.Depth)).ShouldBe(
        [
            ("a doubled calculator", 0),
            ("start with 3", 1),
            ("multiply by 2", 1),
            ("the arithmetic holds", 0),
            ("the value should be 7", 1)
        ]);

        // The narrative step's verdict is an aggregate of what ran inside it, and nothing else.
        render.Steps[0].Status.ShouldBe(ResultStatus.success);
        render.Steps[3].Status.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public void a_declared_index_the_scenario_does_not_have_attributes_to_nothing()
    {
        var uid = "Feature/stale";
        DeclaredSteps.Register(uid, new DeclaredStep("Given", "the only comment", 10));

        using var recording = begin("stale");

        // A stale obj/ from before a comment was deleted. Attributing to the wrong sentence would
        // be a confident lie; attributing to none is merely incomplete.
        using (ScenarioRecorder.Step("Then", "a step from a newer build", 7)) { }

        var render = SpecRender.FromRecording(recording);
        render.Steps.Select(x => x.StepText).ShouldBe(["the only comment", "a step from a newer build"]);
        render.Steps[0].Status.ShouldBe(ResultStatus.ok);
    }

    [Fact]
    public void a_failure_no_step_accounts_for_is_reported_on_the_scenario()
    {
        var uid = "Feature/shouldly";
        DeclaredSteps.Register(uid, new DeclaredStep("Then", "the value should be 7", 10));

        using var recording = begin("shouldly");
        recording.FailureDescription = "Shouldly.ShouldAssertException: should be 7 but was 6";

        var render = SpecRender.FromRecording(recording);

        // The case a projected suite hits most often: the narrative is declared and the verdict is
        // an exception the specification cannot see inside. Without this the report shows three
        // grey lines and no reason at all.
        render.ScenarioFailure.ShouldBe("Shouldly.ShouldAssertException: should be 7 but was 6");
        render.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void a_failure_the_steps_already_explain_is_not_repeated_on_the_scenario()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the value should be 7")) SpecAssert.Check("value", 6, 7);
        recording.FailureDescription = "1 specification step failed: …";

        SpecRender.FromRecording(recording).ScenarioFailure.ShouldBeNull();
    }

    [Fact]
    public void counts_come_from_the_cells_when_a_step_has_them()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the sum and the product"))
        {
            SpecAssert.Check("Sum", 8, 8);
            SpecAssert.Check("Product", 16, 8);
        }

        // A sentence asserting two things made two claims. Counting it as one right understates
        // what the specification covered as much as one wrong overstates what broke.
        SpecRender.FromRecording(recording).Counts.ShouldBe(new Counts(1, 1, 0));
    }

    [Fact]
    public void an_exception_is_an_error_and_an_assertion_is_a_wrong()
    {
        using var recording = begin();

        var step = ScenarioRecorder.Step("When", "the service is called");
        ((IStepHandle)step).Fail(new InvalidOperationException("the naming service is down"));
        step.Dispose();

        var render = SpecRender.FromRecording(recording);
        render.Steps.Single().Status.ShouldBe(ResultStatus.error);
        render.Steps.Single().ExceptionType.ShouldBe("InvalidOperationException");
        render.Counts.ShouldBe(new Counts(0, 0, 1));
    }
}
