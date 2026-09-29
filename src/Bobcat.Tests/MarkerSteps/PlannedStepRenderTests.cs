using Bobcat;
using Bobcat.Engine;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// A projected scenario renders against its <b>plan</b>: the steps it never reached are shown as
/// never reached, and a grammar that spells no keyword gets no keyword.
/// </summary>
public class PlannedStepRenderTests : IDisposable
{
    private const string Uid = "Feature/planned";

    /// <remarks>
    /// Both registries, because both are process-wide: a declared narrative left behind by one test
    /// adds a row to the next one's render, which is how the first version of this class failed.
    /// </remarks>
    public void Dispose()
    {
        PlannedSteps.Clear();
        DeclaredSteps.Clear();
    }

    private static ScenarioRecorder.Recording begin() => ScenarioRecorder.Begin("Feature", "planned", null, Guid.NewGuid());

    private static PlannedStep plan(string keyword, string template, int line, int? declared = null)
        => new(keyword, template, "Grammar.Method", declared, line);

    [Fact]
    public void the_steps_after_a_failure_render_as_never_reached()
    {
        PlannedSteps.Register(Uid,
            plan("When", "the service is called", 10),
            plan("Then", "the value should be {value}", 11),
            plan("Then", "the log is empty", 12));

        using var recording = begin();

        var step = ScenarioRecorder.Step("When", "the service is called", -1, 0);
        ((IStepHandle)step).Fail(new InvalidOperationException("boom"));
        step.Dispose();

        var render = SpecRender.FromRecording(recording);

        render.Steps.Select(x => (x.StepText, x.NotRun)).ShouldBe(
        [
            ("the service is called", false),

            // The templates, unresolved: their arguments were never evaluated, so there are no
            // values, and inventing them would describe a run that did not happen.
            ("the value should be {value}", true),
            ("the log is empty", true)
        ]);

        // Never reached is `ok`, never `success` — nothing observed them.
        render.Steps.Where(x => x.NotRun).ShouldAllBe(x => x.Status == ResultStatus.ok);
    }

    [Fact]
    public void a_step_that_ran_is_not_reported_as_unreached()
    {
        PlannedSteps.Register(Uid, plan("When", "the service is called", 10));

        using var recording = begin();
        using (ScenarioRecorder.Step("When", "the service is called", -1, 0)) { }

        SpecRender.FromRecording(recording).Steps.ShouldAllBe(x => !x.NotRun);
    }

    [Fact]
    public void a_planned_step_called_twice_renders_twice()
    {
        PlannedSteps.Register(Uid, plan("When", "the service is called", 10));

        using var recording = begin();
        using (ScenarioRecorder.Step("When", "the service is called", -1, 0)) { }
        using (ScenarioRecorder.Step("When", "the service is called", -1, 0)) { }

        // A helper inside a loop really ran twice, and the ordinal is a fact about the SOURCE —
        // which is why matching on it survives loops where matching on text would not.
        SpecRender.FromRecording(recording).Steps.Count.ShouldBe(2);
    }

    [Fact]
    public void unreached_steps_nest_under_the_comment_they_sit_under()
    {
        DeclaredSteps.Register(Uid, new DeclaredStep("", "Line assertions", 9));
        PlannedSteps.Register(Uid,
            plan("", "this line throws", 10, declared: 1),
            plan("", "this line is true", 11, declared: 1));

        using var recording = begin();
        var step = ScenarioRecorder.Step("", "this line throws", 0, 0);
        ((IStepHandle)step).Fail(new InvalidOperationException("boom"));
        step.Dispose();

        var render = SpecRender.FromRecording(recording);
        render.Steps.Select(x => (x.StepText, x.Depth)).ShouldBe(
        [
            ("Line assertions", 0),
            ("this line throws", 1),
            ("this line is true", 1)
        ]);
    }

    [Fact]
    public void a_scenario_with_no_plan_reports_nothing_as_unreached()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("When", "a hand written step")) { }

        // Without a plan there is nothing that says a step was ever going to be taken, so claiming
        // one was skipped would be an invention.
        SpecRender.FromRecording(recording).Steps.ShouldAllBe(x => !x.NotRun);
    }

    [Fact]
    public void a_keywordless_step_stays_keywordless_and_is_never_promoted_to_and()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("", "start with the number 5")) { }
        using (ScenarioRecorder.Step("", "multiply by 3")) { }

        // Storyteller and Gauge sentences spell no keyword. The empty string is not a keyword to
        // repeat, so the second step is not `And` — a word the author never wrote.
        recording.Steps.Select(x => x.Keyword).ShouldBe(["", ""]);

        // "" survives the render as "", meaning "no label"; null would mean "fall back to the kind"
        // and would print `Then` over a sentence that never claimed to be one.
        SpecRender.FromRecording(recording).Steps.Select(x => x.Keyword).ShouldBe(["", ""]);
    }

    [Fact]
    public void a_keyword_is_still_promoted_when_there_is_one()
        => renderKeywords(("Given", "a"), ("Given", "b")).ShouldBe(["Given", "And"]);

    private static string?[] renderKeywords(params (string Keyword, string Text)[] steps)
    {
        using var recording = begin();
        foreach (var (keyword, text) in steps)
        {
            using (ScenarioRecorder.Step(keyword, text)) { }
        }

        return SpecRender.FromRecording(recording).Steps.Select(x => x.Keyword).ToArray();
    }

    [Fact]
    public void the_input_values_of_a_sentence_are_marked_for_italics()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Given", "Start with {value}", -1, -1,
                   [new StepArgument("value", 3)]))
        {
        }

        var step = SpecRender.FromRecording(recording).Steps.Single();
        step.StepText.ShouldBe("Start with 3");

        var span = step.ValueSpans.Single();
        step.StepText.Substring(span.Start, span.Length).ShouldBe("3");

        CommandLineRenderer.Sentence(step).ShouldBe("Start with [italic]3[/]");
    }

    [Fact]
    public void a_value_that_also_occurs_in_the_prose_marks_only_the_value()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "3 of them cost {total}", -1, -1,
                   [new StepArgument("total", 3)]))
        {
        }

        // Searching the finished sentence for "3" would italicise the wrong one. The spans come from
        // the substitution itself, which is the only place the distinction exists.
        CommandLineRenderer.Sentence(SpecRender.FromRecording(recording).Steps.Single())
            .ShouldBe("3 of them cost [italic]3[/]");
    }

    [Fact]
    public void an_unresolved_placeholder_is_not_italicised()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "{event} is emitted", -1, -1,
                   [new StepArgument("event", null)]))
        {
        }

        CommandLineRenderer.Sentence(SpecRender.FromRecording(recording).Steps.Single())
            .ShouldBe("{event} is emitted");
    }
}
