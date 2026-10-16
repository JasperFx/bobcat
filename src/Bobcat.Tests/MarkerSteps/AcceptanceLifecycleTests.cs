using System.Reflection;
using System.Text.Json;
using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// bobcat#545: an Acceptance spec runs, is recorded in full under its own verdict, and does not fail
/// the run — and <c>BOBCAT_LIFECYCLE=strict</c> (C13) makes it, and a pending spec, a real failure.
/// The adapters' halves (making the platform report a skip) are tested beside each adapter.
/// </summary>
[Collection("marker-step-run")]
public class AcceptanceLifecycleTests : IDisposable
{
    private readonly RecordingSink _sink = new();
    private readonly string? _strictBefore = Environment.GetEnvironmentVariable(SpecLifecycle.EnvironmentVariable);
    private readonly List<SpecRender> _renders = new();

    public AcceptanceLifecycleTests()
    {
        MarkerStepRun.Reset(_sink);
        Environment.SetEnvironmentVariable(SpecLifecycle.EnvironmentVariable, null);
        ScenarioRecorder.ScenarioCompleted += capture;
    }

    public void Dispose()
    {
        ScenarioRecorder.ScenarioCompleted -= capture;
        Environment.SetEnvironmentVariable(SpecLifecycle.EnvironmentVariable, _strictBefore);
        MarkerStepRun.Reset();
    }

    private void capture(ScenarioRecorder.Recording recording)
    {
        if (recording.Feature != "Acceptance lifecycle") return;
        lock (_renders) _renders.Add(SpecRender.FromRecording(recording));
    }

    private static MethodInfo method(string name) => typeof(LifecycleSpecs).GetMethod(name)!;

    private static ScenarioRecorder.Recording begin(string name)
    {
        MarkerStepRun.StartForTesting(new MonitorRunInfo(Guid.NewGuid(), "Suite", "/repo", "main", "xunit"));
        return MarkerStepRun.BeginScenario(method(name), "xunit");
    }

    private static void gatherAWrong()
    {
        using (ScenarioRecorder.Step("Then", "the balance is 100"))
        {
            SpecAssert.Fact(false, "the balance was 90");
        }
    }

    private ScenarioFinished finished() => _sink.Events.OfType<ScenarioFinished>().ShouldHaveSingleItem();

    [Fact]
    public void the_lifecycle_is_read_off_the_method_then_the_class()
    {
        SpecLifecycle.Of(method(nameof(LifecycleSpecs.declared_acceptance))).ShouldBe(Lifecycle.Acceptance);
        SpecLifecycle.Of(method(nameof(LifecycleSpecs.undeclared))).ShouldBe(Lifecycle.Regression);

        var inClass = typeof(AcceptanceClass).GetMethod(nameof(AcceptanceClass.inherits))!;
        SpecLifecycle.Of(inClass).ShouldBe(Lifecycle.Acceptance);

        // An attribute on the method that never stated a lifecycle must not override its class's...
        var silent = typeof(AcceptanceClass).GetMethod(nameof(AcceptanceClass.states_nothing))!;
        SpecLifecycle.Of(silent).ShouldBe(Lifecycle.Acceptance);

        // ...while one that states Regression promotes that spec on its own.
        var promoted = typeof(AcceptanceClass).GetMethod(nameof(AcceptanceClass.promoted))!;
        SpecLifecycle.Of(promoted).ShouldBe(Lifecycle.Regression);

        // TUnit's shape: a type and a method name.
        SpecLifecycle.Of(typeof(AcceptanceClass), nameof(AcceptanceClass.promoted)).ShouldBe(Lifecycle.Regression);
        SpecLifecycle.Of(typeof(AcceptanceClass), nameof(AcceptanceClass.inherits)).ShouldBe(Lifecycle.Acceptance);
    }

    [Fact]
    public void a_failing_acceptance_spec_is_recorded_in_full_and_the_adapter_is_told_to_skip_it()
    {
        begin(nameof(LifecycleSpecs.declared_acceptance)).Lifecycle.ShouldBe(Lifecycle.Acceptance);

        var close = MarkerStepRun.CloseScenario(
            ScenarioVerdict.Failed("System.NotImplementedException", "the behaviour"));

        close.Skip.ShouldBeTrue();
        close.Rethrow.ShouldBeNull();
        close.SkipReason.ShouldBe("acceptance spec, not yet passing: System.NotImplementedException: the behaviour");

        var published = finished();
        published.Outcome.ShouldBe(ScenarioRecorder.Recording.AcceptanceFailingOutcome);
        published.Lifecycle.ShouldBe("Acceptance");
        published.ErrorMessage.ShouldBe("System.NotImplementedException: the behaviour", "nothing is swallowed");

        MarkerStepRun.Counts.ShouldBe((0, 0));
        MarkerStepRun.AcceptanceFailingCount.ShouldBe(1);
    }

    [Fact]
    public void gathered_wrongs_skip_with_the_first_failed_step_rather_than_rethrowing()
    {
        begin(nameof(LifecycleSpecs.declared_acceptance));
        gatherAWrong();

        var close = MarkerStepRun.CloseScenario(ScenarioVerdict.Passed);

        close.Rethrow.ShouldBeNull();
        close.SkipReason.ShouldNotBeNull().ShouldStartWith(SpecLifecycle.SkipReasonPrefix);
        close.SkipReason.ShouldContain("the balance is 100");
        close.SkipReason.ShouldNotContain("specification step failed");

        finished().Outcome.ShouldBe("AcceptanceFailing");

        var render = _renders.ShouldHaveSingleItem();
        render.Succeeded.ShouldBeFalse();
        render.AcceptanceFailing.ShouldBeTrue();
        render.FailsTheRun.ShouldBeFalse();
        render.Verdict.ShouldBe("acceptance: failing");
        render.Steps.ShouldNotBeEmpty("full steps and cells, not a summary");
    }

    [Fact]
    public void a_passing_acceptance_spec_is_ready_to_promote()
    {
        begin(nameof(LifecycleSpecs.declared_acceptance));

        MarkerStepRun.CloseScenario(ScenarioVerdict.Passed).ShouldBe(ScenarioClose.Nothing);

        var published = finished();
        published.Outcome.ShouldBe("CleanPass");
        published.Lifecycle.ShouldBe("Acceptance");

        var render = _renders.ShouldHaveSingleItem();
        render.ReadyToPromote.ShouldBeTrue();
        render.Verdict.ShouldBe("acceptance: passing");
    }

    [Fact]
    public void a_regression_spec_is_unchanged()
    {
        begin(nameof(LifecycleSpecs.undeclared));
        gatherAWrong();

        var close = MarkerStepRun.CloseScenario(ScenarioVerdict.Passed);

        close.Skip.ShouldBeFalse();
        close.Rethrow.ShouldBeOfType<SpecAssertionException>();

        var published = finished();
        published.Outcome.ShouldBe("Failed");
        published.Lifecycle.ShouldBeNull("Regression is the default and is not stated on the wire");
    }

    [Fact]
    public void strict_mode_makes_an_acceptance_failure_a_real_failure()
    {
        Environment.SetEnvironmentVariable(SpecLifecycle.EnvironmentVariable, "Strict");

        begin(nameof(LifecycleSpecs.declared_acceptance));
        gatherAWrong();

        var close = MarkerStepRun.CloseScenario(ScenarioVerdict.Passed);

        close.Skip.ShouldBeFalse();
        close.Rethrow.ShouldBeOfType<SpecAssertionException>();

        var published = finished();
        published.Outcome.ShouldBe("Failed");
        published.Lifecycle.ShouldBe("Acceptance");

        _renders.ShouldHaveSingleItem().Verdict.ShouldBe("failed");
        MarkerStepRun.Counts.ShouldBe((0, 1));
    }

    [Fact]
    public void the_run_counts_acceptance_failures_apart_and_they_do_not_fail_it()
    {
        begin(nameof(LifecycleSpecs.declared_acceptance));
        MarkerStepRun.CloseScenario(ScenarioVerdict.Failed(null, "not yet"));

        begin(nameof(LifecycleSpecs.undeclared));
        MarkerStepRun.CloseScenario(ScenarioVerdict.Passed);

        MarkerStepRun.FinishForTesting();

        var run = _sink.Events.OfType<RunFinished>().ShouldHaveSingleItem();
        run.ExitCode.ShouldBe(0);
        run.Passed.ShouldBe(1);
        run.Failed.ShouldBe(0);
        run.AcceptanceFailing.ShouldBe(1);
    }

    [Fact]
    public void a_pending_spec_in_strict_mode_fails_with_its_identity_on_every_surface()
    {
        MarkerStepRun.StartForTesting(new MonitorRunInfo(Guid.NewGuid(), "Suite", "/repo", "main", "xunit"));

        var failure = MarkerStepRun.FailPendingInStrictMode(method(nameof(LifecycleSpecs.undeclared)), "xunit");

        failure.Message.ShouldBe("pending specification in strict mode: Acceptance lifecycle/undeclared");
        ScenarioRecorder.Current.ShouldBeNull("the scenario is opened and closed in one go");

        var published = finished();
        published.Outcome.ShouldBe("Failed");
        published.ErrorMessage.ShouldBe(failure.Message);
    }

    [Fact]
    public void the_json_report_carries_the_verdict_and_the_lifecycle()
    {
        begin(nameof(LifecycleSpecs.declared_acceptance));
        gatherAWrong();
        MarkerStepRun.CloseScenario(ScenarioVerdict.Passed);

        using var json = JsonDocument.Parse(JsonRenderer.RenderScenario(_renders.ShouldHaveSingleItem()));

        json.RootElement.GetProperty("verdict").GetString().ShouldBe("acceptance: failing");
        json.RootElement.GetProperty("specLifecycle").GetString().ShouldBe("acceptance");
        json.RootElement.GetProperty("succeeded").GetBoolean().ShouldBeFalse();
        json.RootElement.GetProperty("steps").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void the_console_names_the_verdict_and_never_says_failed()
    {
        begin(nameof(LifecycleSpecs.declared_acceptance));
        gatherAWrong();
        MarkerStepRun.CloseScenario(ScenarioVerdict.Passed);

        var text = string.Join('\n', PlainSpecText.Lines(_renders.ShouldHaveSingleItem()));

        text.ShouldContain("ACCEPTANCE: FAILING");
        text.ShouldContain("does not fail the build");
        text.ShouldContain("the balance is 100");
        text.ShouldNotContain(" FAILED", Case.Sensitive);
    }

    [Theory]
    [InlineData("1 specification step failed:\n  Then the balance is 100 => was 90", "Then the balance is 100 => was 90")]
    [InlineData("System.Exception: boom", "System.Exception: boom")]
    [InlineData("", "Failed")]
    public void the_first_failure_skips_the_gathered_heading(string description, string expected)
        => SpecLifecycle.FirstFailure(description).ShouldBe(expected);

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    private sealed class DeclaresAttribute : Attribute, IDeclaresLifecycle
    {
        private Lifecycle? _lifecycle;

        public Lifecycle Lifecycle
        {
            get => _lifecycle ?? Lifecycle.Regression;
            set => _lifecycle = value;
        }

        Lifecycle? IDeclaresLifecycle.Lifecycle => _lifecycle;
    }

    [BobcatFeature("Acceptance lifecycle")]
    public class LifecycleSpecs
    {
        [Declares(Lifecycle = Lifecycle.Acceptance)]
        public void declared_acceptance() { }

        public void undeclared() { }
    }

    [Declares(Lifecycle = Lifecycle.Acceptance)]
    public class AcceptanceClass
    {
        public void inherits() { }

        [Declares]
        public void states_nothing() { }

        [Declares(Lifecycle = Lifecycle.Regression)]
        public void promoted() { }
    }

    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = new();

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }
    }
}
