using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Engine;

/// <summary>
/// The typed per-scenario blackboard (issue #212 phase 1): state published on one
/// <see cref="IStepContext"/> is readable by anything holding the same context, keyed by type,
/// and structurally invisible to any other context — which is what "cleared with the scenario
/// scope" means, because the runner builds a fresh context per attempt.
/// </summary>
public class ScenarioStateTests
{
    private sealed record HttpCapture(int StatusCode);

    private sealed record OtherCapture(string Name);

    [Fact]
    public void set_then_get_round_trips_by_type()
    {
        IStepContext context = new SpecExecutionContext("spec");

        context.SetState(new HttpCapture(201));

        context.GetState<HttpCapture>().StatusCode.ShouldBe(201);
    }

    [Fact]
    public void a_later_set_replaces_the_earlier_entry_of_the_same_type()
    {
        IStepContext context = new SpecExecutionContext("spec");

        context.SetState(new HttpCapture(200));
        context.SetState(new HttpCapture(400));

        context.GetState<HttpCapture>().StatusCode.ShouldBe(400);
    }

    [Fact]
    public void entries_of_different_types_do_not_collide()
    {
        IStepContext context = new SpecExecutionContext("spec");

        context.SetState(new HttpCapture(200));
        context.SetState(new OtherCapture("wallet"));

        context.GetState<HttpCapture>().StatusCode.ShouldBe(200);
        context.GetState<OtherCapture>().Name.ShouldBe("wallet");
    }

    [Fact]
    public void get_state_on_a_missing_entry_throws_a_step_authoring_diagnostic()
    {
        IStepContext context = new SpecExecutionContext("spec");

        var ex = Should.Throw<InvalidOperationException>(() => context.GetState<HttpCapture>());

        ex.Message.ShouldContain("No step in this scenario produced a HttpCapture");
        ex.Message.ShouldContain("When");
    }

    [Fact]
    public void try_get_state_reports_absence_without_throwing()
    {
        IStepContext context = new SpecExecutionContext("spec");

        context.TryGetState<HttpCapture>(out _).ShouldBeFalse();

        context.SetState(new HttpCapture(204));
        context.TryGetState<HttpCapture>(out var capture).ShouldBeTrue();
        capture!.StatusCode.ShouldBe(204);
    }

    [Fact]
    public void state_is_scoped_to_one_context_instance_which_is_the_scenario_bracket()
    {
        // The runner builds a fresh SpecExecutionContext per attempt, so per-context is
        // per-scenario — a second scenario (or a retry) starts blank.
        IStepContext first = new SpecExecutionContext("spec");
        IStepContext second = new SpecExecutionContext("spec");

        first.SetState(new HttpCapture(500));

        second.TryGetState<HttpCapture>(out _).ShouldBeFalse();
        first.GetState<HttpCapture>().StatusCode.ShouldBe(500);
    }

    [Fact]
    public void value_typed_state_is_supported()
    {
        IStepContext context = new SpecExecutionContext("spec");

        context.SetState(42);

        context.GetState<int>().ShouldBe(42);
    }

    [Fact]
    public void any_context_implementation_gets_a_working_blackboard_from_the_defaults()
    {
        // The members are default interface members backed by a per-instance store, so a narrow
        // hand-rolled context — a test fake, a foreign host — carries state without implementing
        // anything, and adding the contract broke no existing IStepContext implementer.
        IStepContext context = new BareContext();

        context.SetState(new HttpCapture(418));

        context.GetState<HttpCapture>().StatusCode.ShouldBe(418);
    }

    private sealed class BareContext : IStepContext
    {
        public string SpecId => "bare";

        public T GetService<T>() where T : notnull => throw new NotSupportedException();

        public T GetResource<T>(string? name = null) where T : class, ITestResource
            => throw new NotSupportedException();

        public void Log(string message)
        {
        }

        public void AttachDiagnostic(string key, object data)
        {
        }

        public void ReportProgress(StepUpdate update)
        {
        }

        public CancellationToken Cancellation => CancellationToken.None;
    }
}
