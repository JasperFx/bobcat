using System.Reflection;
using Bobcat.Monitoring;
using Bobcat.Tests.Monitoring;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// Bobcat#488: the projected run bracket publishes the spec assembly's half of the Event Model, the
/// job only the Gherkin runner used to do. These drive <see cref="MarkerStepRun"/> against a real
/// <see cref="MonitorTargets"/> over real HTTP to <see cref="FakeMonitorHost"/>; the half itself is
/// handed in through <see cref="MarkerStepRun.FindEventModelHalves"/>, because this assembly declares
/// no slices. The real generated half, in a real projected host, is
/// <c>Bobcat.Supervisor.Tests.ProjectedEventModelPublishingEndToEndTests</c>.
/// </summary>
/// <remarks>
/// In the <c>marker-step-run</c> collection because the bracket is process-wide state. That
/// collection runs alone, which is also what makes clearing <c>BOBCAT_MONITOR</c> here safe:
/// CI sets it to 0, and <see cref="MonitorTargets.TryConnect"/> would refuse to build a publisher.
/// </remarks>
[Collection("marker-step-run")]
public class ProjectedEventModelPublishingTests : IDisposable
{
    private readonly string? _previousKillSwitch;
    private readonly List<string> _notices = new();
    private readonly List<Assembly?> _asked = new();

    public ProjectedEventModelPublishingTests()
    {
        _previousKillSwitch = Environment.GetEnvironmentVariable(MonitorPublisher.KillSwitchVariable);
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, null);
    }

    public void Dispose()
    {
        MarkerStepRun.Reset();
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, _previousKillSwitch);
    }

    private static SpecEventModelPublisher.Half half(string model = "BankAccountES")
        => new(
            "BankAccountES.Tests",
            SpecEventModelPublisher.SourceNameFor("BankAccountES.Tests"),
            new EventModelDescriptor(model, [
                new EventModelSliceDescriptor("EnrollClient", null, null, null, null, [], [], [])
                {
                    Specifications = [new SpecificationDescriptor("Bank Account Event Sourcing/Enroll a client")]
                }
            ]));

    /// <summary>Install <paramref name="sink"/> as the bracket's sink and a fixed half to publish.</summary>
    private void attach(IMonitorEventSink? sink, string model = "BankAccountES")
    {
        MarkerStepRun.Reset(sink);
        MarkerStepRun.FindEventModelHalves = assembly =>
        {
            lock (_asked) _asked.Add(assembly);
            return [half(model)];
        };
        MarkerStepRun.EventModelNotice = notice =>
        {
            lock (_notices) _notices.Add(notice);
        };
    }

    private static void runOneScenario(string method)
    {
        MarkerStepRun.BeginScenario(typeof(WalletSpecs).GetMethod(method)!, "xunit");
        MarkerStepRun.EndScenario(ScenarioVerdict.Passed);
    }

    [Fact]
    public async Task a_run_publishes_the_half_once_however_many_scenarios_it_opens()
    {
        using var host = new FakeMonitorHost();
        await using var publisher = await MonitorTargets.TryConnect([host.Url]);
        publisher.ShouldNotBeNull();
        attach(publisher);

        // The session hook opens the bracket, then every scenario asks for it again.
        MarkerStepRun.OpenRun("xunit");
        runOneScenario(nameof(WalletSpecs.a_credit_raises_the_balance));
        runOneScenario(nameof(WalletSpecs.a_debit_lowers_it));
        MarkerStepRun.CloseRun();

        var push = host.EventModelPushes.ShouldHaveSingleItem();
        push.Source.ShouldBe("BankAccountES-Tests");
        push.Body.ShouldContain("\"identity\":\"Bank Account Event Sourcing/Enroll a client\"");
        _notices.ShouldBeEmpty();
    }

    /// <summary>
    /// A runner with no session hook (TUnit, NUnit, MSTest, or VSTest) opens the bracket on its
    /// first scenario, and that is still exactly one publish for the run.
    /// </summary>
    [Fact]
    public async Task a_bracket_opened_by_its_first_scenario_publishes_once_and_looks_in_that_scenarios_assembly()
    {
        using var host = new FakeMonitorHost();
        await using var publisher = await MonitorTargets.TryConnect([host.Url]);
        publisher.ShouldNotBeNull();
        attach(publisher);

        runOneScenario(nameof(WalletSpecs.a_credit_raises_the_balance));
        runOneScenario(nameof(WalletSpecs.a_debit_lowers_it));
        MarkerStepRun.CloseRun();

        host.EventModelPushes.ShouldHaveSingleItem();
        _asked.ShouldBe([typeof(WalletSpecs).Assembly]);
    }

    /// <summary>
    /// The Gherkin runner published every time it attached, and a resident runner attached once
    /// per command, cold or warm. A warm projected host opens one bracket per run request, so it
    /// does the same: same source each time, which the console treats as a replacement.
    /// </summary>
    [Fact]
    public async Task each_run_request_in_a_warm_process_publishes_again_under_the_same_source()
    {
        using var host = new FakeMonitorHost();
        await using var publisher = await MonitorTargets.TryConnect([host.Url]);
        publisher.ShouldNotBeNull();
        attach(publisher);

        MarkerStepRun.OpenRun("xunit");
        runOneScenario(nameof(WalletSpecs.a_credit_raises_the_balance));
        MarkerStepRun.CloseRun();

        MarkerStepRun.OpenRun("xunit");
        runOneScenario(nameof(WalletSpecs.a_debit_lowers_it));
        MarkerStepRun.CloseRun();

        host.EventModelPushes.Select(p => p.Source).ShouldBe(["BankAccountES-Tests", "BankAccountES-Tests"]);
    }

    [Fact]
    public void nothing_is_published_or_even_looked_for_when_no_console_is_attached()
    {
        attach(sink: null);

        MarkerStepRun.OpenRun("xunit");
        runOneScenario(nameof(WalletSpecs.a_credit_raises_the_balance));
        MarkerStepRun.CloseRun();

        _asked.ShouldBeEmpty();
        _notices.ShouldBeEmpty();
    }

    /// <summary>
    /// A sink that is not the HTTP publisher (a supervisor's in-process sink, a test's recorder)
    /// has no console behind it to take a half.
    /// </summary>
    [Fact]
    public void a_sink_with_no_console_behind_it_publishes_nothing()
    {
        attach(new NullSink());

        MarkerStepRun.OpenRun("xunit");
        MarkerStepRun.CloseRun();

        _asked.ShouldBeEmpty();
    }

    /// <summary>
    /// The same-model guard. <c>GET /api/event-model</c> merges only the sources naming the
    /// console's current model, so a half naming another one would hide the other half rather than
    /// join it. It is withheld, and the reason goes to the notice.
    /// </summary>
    [Fact]
    public async Task a_half_naming_a_different_model_is_withheld_and_the_run_is_told_why()
    {
        using var host = new FakeMonitorHost { CurrentEventModelName = "BankAccountES" };
        await using var publisher = await MonitorTargets.TryConnect([host.Url]);
        publisher.ShouldNotBeNull();
        attach(publisher, model: "BankAccountES.Tests");

        MarkerStepRun.OpenRun("xunit");
        runOneScenario(nameof(WalletSpecs.a_credit_raises_the_balance));
        MarkerStepRun.CloseRun();

        host.EventModelPushes.ShouldBeEmpty();

        var notice = _notices.ShouldHaveSingleItem();
        notice.ShouldContain("'BankAccountES'");
        notice.ShouldContain("[assembly: EventModelName(\"BankAccountES\")]");
    }

    [Fact]
    public async Task a_half_that_will_not_load_does_not_disturb_the_run()
    {
        using var host = new FakeMonitorHost();
        await using var publisher = await MonitorTargets.TryConnect([host.Url]);
        publisher.ShouldNotBeNull();
        MarkerStepRun.Reset(publisher);
        MarkerStepRun.FindEventModelHalves = _ => throw new InvalidOperationException("no model here");

        Should.NotThrow(() => MarkerStepRun.OpenRun("xunit"));
        MarkerStepRun.CloseRun();

        host.EventModelPushes.ShouldBeEmpty();
    }

    /// <summary>
    /// The default lookup: this assembly declares no slices and neither does the test host, so
    /// there is nothing to publish and no request is made.
    /// </summary>
    [Fact]
    public async Task an_assembly_with_no_slices_sends_nothing()
    {
        using var host = new FakeMonitorHost();
        await using var publisher = await MonitorTargets.TryConnect([host.Url]);
        publisher.ShouldNotBeNull();
        MarkerStepRun.Reset(publisher);

        runOneScenario(nameof(WalletSpecs.a_credit_raises_the_balance));
        MarkerStepRun.CloseRun();

        host.EventModelPushes.ShouldBeEmpty();
    }

    [BobcatFeature("Wallet")]
    public class WalletSpecs
    {
        public void a_credit_raises_the_balance() { }

        public void a_debit_lowers_it() { }
    }

    private sealed class NullSink : IMonitorEventSink
    {
        public void Post(MonitorEvent @event) { }
    }
}
