using Bobcat;
using Bobcat.Engine;
using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// A step's full report reaches the wire — cells, the plan, the input-value spans and a real
/// exception's stack.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this pins, and why it is worth its own file.</b> Everything here was renderable locally
/// and unreachable remotely. <c>StepFinished</c> carried one flattened failure string, so the most
/// valuable thing in a specification report — <i>expected 6, got 8</i>, per named cell, with the
/// right cells green beside the wrong one — could not be shown by any consumer of the wire, in
/// either authoring lane. The events in <c>MonitorEvents.cs</c> are read by a console with no
/// assembly reference to warn it, so a field that is missing is missing silently.
/// </para>
/// </remarks>
public class StepReportWireTests : IDisposable
{
    private const string Uid = "Feature/Scenario";

    private readonly RecordingSink _sink = new();
    private readonly Guid _runId = Guid.NewGuid();

    public void Dispose()
    {
        DeclaredSteps.Clear();
        PlannedSteps.Clear();
    }

    private ScenarioRecorder.Recording begin()
        => ScenarioRecorder.Begin("Feature", "Scenario", _sink, _runId);

    // --- issue #387: cells so far, while the step is still running ---

    [Fact]
    public void a_running_step_publishes_the_cells_it_has_made_so_far()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the totals are"))
        {
            SpecAssert.Check("Sum", 8, 6);
            SpecAssert.Check("Product", 16, 16);
        }

        // At least one interim update, carrying the whole set as of that moment rather than a delta.
        var progress = _sink.Events.OfType<StepProgress>().ToArray();
        progress.ShouldNotBeEmpty();
        progress.ShouldAllBe(p => p.StepId == "s1");

        var last = progress[^1];
        last.Cells.ShouldNotBeNull();
        last.Cells!.Select(c => c.Name).ShouldBeSubsetOf(["Sum", "Product"]);
        last.Cells[0].Name.ShouldBe("Sum");
        last.Cells[0].Expected.ShouldBe("6");
        last.Cells[0].Actual.ShouldBe("8");
    }

    [Fact]
    public void the_final_cells_are_still_the_authority()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the totals are"))
        {
            SpecAssert.Check("Sum", 8, 6);
            SpecAssert.Check("Product", 16, 16);
        }

        // Whatever the interim updates managed to say, StepFinished carries the complete set — so an
        // interim update the coalescer threw away can never leave a wrong final picture.
        _sink.Events.OfType<StepFinished>().Single().Cells!
            .Select(c => c.Name).ShouldBe(["Sum", "Product"]);
    }

    [Fact]
    public void five_hundred_cells_in_a_tight_loop_do_not_become_five_hundred_payloads()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "every row agrees"))
        {
            for (var i = 0; i < 500; i++) SpecAssert.Check($"row{i}", i, i);
        }

        // Coalesced on the same interval the engine lane uses. The channel drops on backpressure and
        // StepFinished matters more than any tick, so the interim view is deliberately cheap.
        _sink.Events.OfType<StepProgress>().Count().ShouldBeLessThan(20);
    }

    [Fact]
    public void a_check_outside_a_scenario_publishes_nothing_and_throws_nothing()
    {
        // SpecAssert is called from helpers that are not specifications. With nothing recording there
        // is nothing to report to, and saying so must not cost the caller an exception.
        SpecAssert.Check("loose", 1, 2).ShouldBeFalse();

        _sink.Events.ShouldBeEmpty();
    }

    [Fact]
    public void a_value_comparison_reaches_the_wire_as_a_cell()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the sum and the product"))
        {
            SpecAssert.Check("Sum", 8, 6);
            SpecAssert.Check("Product", 16, 16);
        }

        var finished = _sink.Events.OfType<StepFinished>().ShouldHaveSingleItem();
        var cells = finished.Cells.ShouldNotBeNull();

        // Both of them: a wrong product must not hide a right sum on the way out either.
        cells.Count.ShouldBe(2);

        var sum = cells.Single(x => x.Name == "Sum");
        sum.Status.ShouldBe("failed");
        sum.Expected.ShouldBe("6");
        sum.Actual.ShouldBe("8");

        cells.Single(x => x.Name == "Product").Status.ShouldBe("success");
    }

    [Fact]
    public void a_gathered_wrong_carries_no_stack_trace()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the email was sent"))
        {
            SpecAssert.Fail("The email server is not reachable");
        }

        var finished = _sink.Events.OfType<StepFinished>().ShouldHaveSingleItem();
        finished.ErrorMessage.ShouldBe("The email server is not reachable");

        // Nothing was thrown, so there is nothing to show. A stack here would dress a clean failure
        // message up as a crash, which is the distinction Storyteller's own assert existed to keep.
        finished.ExceptionType.ShouldBeNull();
        finished.StackTrace.ShouldBeNull();
    }

    [Fact]
    public void a_thrown_exception_carries_its_type_and_stack()
    {
        using var recording = begin();

        var step = ScenarioRecorder.Step("When", "the service is called");
        try
        {
            throw new InvalidOperationException("the naming service is down");
        }
        catch (Exception e)
        {
            ((IStepHandle)step).Fail(e);
        }

        step.Dispose();

        var finished = _sink.Events.OfType<StepFinished>().ShouldHaveSingleItem();
        finished.ExceptionType.ShouldBe("InvalidOperationException");
        finished.StackTrace.ShouldNotBeNull();

        // The filtered frames travel too, because which frames are Bobcat's own interceptors and step
        // brackets is Bobcat's knowledge — not something a console can be expected to know, and two
        // copies of that list would drift the moment either side moved a type. The raw stack stays, so
        // a viewer can always offer "show every frame".
        finished.StackFrames.ShouldNotBeNull()
            .ShouldContain(x => x.Contains(nameof(a_thrown_exception_carries_its_type_and_stack)));
    }

    [Fact]
    public void the_plan_is_announced_with_the_scenario()
    {
        PlannedSteps.Register(Uid,
            new PlannedStep("When", "the service is called", "Grammar.Call", null, 10),
            new PlannedStep("Then", "the value should be {value}", "Grammar.Value", null, 11));

        using var recording = begin();

        var started = _sink.Events.OfType<ScenarioStarted>().ShouldHaveSingleItem();
        var planned = started.PlannedSteps.ShouldNotBeNull();

        // Templates, unresolved — at plan time no argument has been evaluated. And the grammar, which
        // is the answer to "why did my step bind to that method".
        planned.Select(x => x.Template).ShouldBe(["the service is called", "the value should be {value}"]);
        planned[1].Grammar.ShouldBe("Grammar.Value");

        // The count comes from the plan when there is no declared narrative, so a watcher can render
        // "step 1 of 2" for a projected test that uses no marker comments at all.
        started.TotalSteps.ShouldBe(2);
    }

    [Fact]
    public void a_recorded_step_says_which_planned_step_it_is()
    {
        PlannedSteps.Register(Uid, new PlannedStep("When", "the service is called", "Grammar.Call", null, 10));

        using var recording = begin();
        using (ScenarioRecorder.Step("When", "the service is called", -1, 0)) { }

        _sink.Events.OfType<StepStarted>().ShouldHaveSingleItem().PlannedStepNumber.ShouldBe(1);
    }

    [Fact]
    public void the_input_value_spans_travel_with_the_sentence()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Given", "Start with {value}", -1, -1,
                   [new StepArgument("value", 3)]))
        {
        }

        var started = _sink.Events.OfType<StepStarted>().ShouldHaveSingleItem();
        started.Text.ShouldBe("Start with 3");

        // Carried rather than recomputed: a viewer searching the sentence for "3" would italicise
        // the wrong characters whenever the value also occurs in the prose.
        var span = started.Values.ShouldNotBeNull().ShouldHaveSingleItem();
        started.Text.Substring(span.Start, span.Length).ShouldBe("3");
    }

    [Fact]
    public void a_step_with_nothing_extra_to_say_carries_nulls_rather_than_empty_lists()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Given", "a plain step")) { }

        var finished = _sink.Events.OfType<StepFinished>().ShouldHaveSingleItem();

        // Null, not empty: every one of these is optional and additive, and an empty list on the wire
        // is a claim that the step was asked and had nothing, which is a different thing from not
        // being asked.
        finished.Cells.ShouldBeNull();
        finished.Columns.ShouldBeNull();
        finished.Logs.ShouldBeNull();
        finished.Diagnostics.ShouldBeNull();
        finished.StackFrames.ShouldBeNull();
        finished.HiddenStackFrames.ShouldBe(0);
        _sink.Events.OfType<StepStarted>().Single().Values.ShouldBeNull();
    }

    /// <remarks>Local, like the two beside it: a shared fake is one more thing a test has to agree with.</remarks>
    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = new();

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }
    }
}
