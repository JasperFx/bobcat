using Bobcat;
using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// <see cref="SpecAssert"/>'s polling checks and the tolerance overload: what replaced the removed
/// Gherkin lane's <c>[WaitFor]</c> and <c>[Approx]</c>.
/// </summary>
public class SpecAssertEventuallyTests
{
    private static readonly TimeSpan every = TimeSpan.FromMilliseconds(5);

    private static ScenarioRecorder.Recording begin()
        => ScenarioRecorder.Begin("Feature", "Scenario", null, Guid.NewGuid());

    [Fact]
    public void a_tolerance_is_an_approximate_comparison_with_its_note()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the average is 2.5"))
        {
            SpecAssert.Check("average", 2.46, 2.5, tolerance: 0.1).ShouldBeTrue();
            SpecAssert.Check("average", 2.3, 2.5, tolerance: 0.1).ShouldBeFalse();
        }

        var cells = recording.Steps.Single().Cells;
        cells[0].Status.ShouldBe(ResultStatus.success);
        cells[0].Note.ShouldBe("±0.1");
        cells[1].Status.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public async Task a_value_that_converges_records_one_cell_however_many_polls_it_took()
    {
        var calls = 0;
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the count becomes 0"))
        {
            (await SpecAssert.EventuallyAsync("count", () => ++calls >= 4 ? 0 : 5, 0, TimeSpan.FromSeconds(2), every))
                .ShouldBeTrue();
        }

        calls.ShouldBe(4);
        var cell = recording.Steps.Single().Cells.ShouldHaveSingleItem();
        cell.Status.ShouldBe(ResultStatus.success);
        cell.Note.ShouldNotBeNull().ShouldContain("(4 polls)");
    }

    [Fact]
    public async Task an_asynchronous_value_is_awaited_on_each_poll()
    {
        var calls = 0;
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the status becomes Ready"))
        {
            (await SpecAssert.EventuallyAsync("status", async () =>
            {
                await Task.Yield();
                return ++calls >= 2 ? "Ready" : "Pending";
            }, "Ready", TimeSpan.FromSeconds(2), every)).ShouldBeTrue();
        }

        recording.Steps.Single().Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task a_throwing_read_is_not_ready_yet_and_the_last_error_is_named_on_timeout()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the count becomes 0"))
        {
            (await SpecAssert.EventuallyAsync("count", new Func<int>(() => throw new InvalidOperationException("no document yet")),
                0, TimeSpan.FromMilliseconds(30), every)).ShouldBeFalse();
        }

        var step = recording.Steps.Single();
        step.Status.ShouldBe(ResultStatus.failed);
        step.Failure.ShouldBeNull();

        var cell = step.Cells.ShouldHaveSingleItem();
        cell.Expected.ShouldBe("0");
        cell.Note.ShouldNotBeNull().ShouldContain("last error: no document yet");
    }

    [Fact]
    public async Task a_fact_that_never_comes_true_says_why_and_how_long_it_waited()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Then", "the system is ready"))
        {
            (await SpecAssert.EventuallyAsync(() => false, TimeSpan.FromMilliseconds(30), every, because: "the node never joined"))
                .ShouldBeFalse();
        }

        var cell = recording.Steps.Single().Cells.ShouldHaveSingleItem();
        cell.Name.ShouldBe(SpecAssert.EventuallyCell);
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.DisplayText.ShouldStartWith("the node never joined; timed out after 30ms @5ms");
    }

    [Fact]
    public async Task an_action_that_keeps_throwing_is_a_wrong_naming_its_last_error()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("When", "the queue drains"))
        {
            (await SpecAssert.EventuallyAsync(new Action(() => throw new InvalidOperationException("3 left")),
                TimeSpan.FromMilliseconds(30), every)).ShouldBeFalse();
        }

        var step = recording.Steps.Single();
        step.Status.ShouldBe(ResultStatus.failed);
        step.Cells.Single().Note.ShouldNotBeNull().ShouldContain("last error: 3 left");
    }

    [Fact]
    public async Task a_critical_exception_is_not_retried()
    {
        var calls = 0;
        await Should.ThrowAsync<SpecCriticalException>(() => SpecAssert.EventuallyAsync(new Action(() =>
        {
            calls++;
            throw new SpecCriticalException("stop here");
        }), TimeSpan.FromSeconds(2), every));

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task outside_a_scenario_it_still_polls_and_answers()
    {
        var calls = 0;
        (await SpecAssert.EventuallyAsync(() => ++calls >= 2, TimeSpan.FromSeconds(2), every)).ShouldBeTrue();
        calls.ShouldBe(2);
    }
}
