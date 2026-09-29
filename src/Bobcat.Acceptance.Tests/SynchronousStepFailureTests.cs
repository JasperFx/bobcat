using Bobcat;
using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// A <c>[BobcatStep]</c> helper that throws is the step that failed, whether or not it was
/// asynchronous — proved against the real generator, in a real compilation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this pins.</b> The emitted interceptor wrapped a synchronous call in
/// <c>using (step)</c>. Disposal runs on the way out either way, and disposal alone means only "the
/// step ended" — so a step that threw was recorded as having passed. The asynchronous path never had
/// it, because <c>MarkerStepRuntime.Track</c> has always caught and reported.
/// </para>
/// <para>
/// <b>Why it hid for so long.</b> The scenario was still red: the runner saw the exception and
/// failed the test. The only symptom was that the failing line of the specification was the one
/// marked <c>✓</c> — and nothing rendered a projected specification locally, so nobody was reading
/// the lines.
/// </para>
/// </remarks>
[BobcatFeature("Synchronous step failures")]
public class SynchronousStepFailureTests
{
    [BobcatStep("the synchronous action blows up", Keyword = "When")]
    internal void ActionThatThrows() => throw new InvalidOperationException("boom");

    [BobcatStep("the synchronous value is read", Keyword = "When")]
    internal int ValueThatThrows() => throw new InvalidOperationException("boom");

    [BobcatStep("the asynchronous action blows up", Keyword = "When")]
    internal async Task AsyncActionThatThrows()
    {
        await Task.Yield();
        throw new InvalidOperationException("boom");
    }

    [BobcatStep("the synchronous value is {value}", Keyword = "Then")]
    internal int Value(int value) => value;

    private static ScenarioRecorder.Recording begin(string scenario)
        => ScenarioRecorder.Begin("Synchronous step failures", scenario, null, Guid.NewGuid());

    [Fact]
    public void a_void_step_that_throws_is_recorded_as_an_error()
    {
        using var recording = begin("a void step that throws is recorded as an error");

        Should.Throw<InvalidOperationException>(() => ActionThatThrows());

        var step = recording.Steps.Single();
        step.Status.ShouldBe(ResultStatus.error);
        step.Failure.ShouldBeOfType<InvalidOperationException>();

        // And the step still ended, so the report has a duration rather than an open step.
        step.EndedAtMs.ShouldNotBeNull();
    }

    [Fact]
    public void a_value_returning_step_that_throws_is_recorded_as_an_error()
    {
        using var recording = begin("a value returning step that throws is recorded as an error");

        Should.Throw<InvalidOperationException>(() => ValueThatThrows());

        recording.Steps.Single().Status.ShouldBe(ResultStatus.error);
    }

    [Fact]
    public async Task an_async_step_that_throws_is_recorded_as_an_error()
    {
        using var recording = begin("an async step that throws is recorded as an error");

        await Should.ThrowAsync<InvalidOperationException>(() => AsyncActionThatThrows());

        recording.Steps.Single().Status.ShouldBe(ResultStatus.error);
    }

    [Fact]
    public void a_value_returning_step_that_does_not_throw_still_returns_its_value()
    {
        using var recording = begin("a value returning step that does not throw still returns its value");

        // The bracket must not change what the helper hands back, or every projected suite using a
        // value-returning helper would break on adoption.
        Value(42).ShouldBe(42);

        recording.Steps.Single().Status.ShouldBe(ResultStatus.success);
    }
}
