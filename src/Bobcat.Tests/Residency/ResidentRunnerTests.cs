using Bobcat.Residency;
using Bobcat.Runtime;
using Bobcat.Tests.Monitoring;
using Shouldly;

namespace Bobcat.Tests.Residency;

/// <summary>
/// Issue #390: a suite kept available to a monitor, running specifications when the monitor asks.
/// Driven against <see cref="FakeMonitorHost"/> over real HTTP, because the invariant being tested
/// is about an absent, slow or hostile monitor and a seam in front of the transport would test the
/// seam instead.
/// </summary>
public class ResidentRunnerTests
{
    private sealed class RecordingSuite : IResidentSuite
    {
        public readonly List<(string CommandId, string Mode, IReadOnlyList<string> Specs)> Runs = new();

        public string Suite => "Orders.Specs";
        public string Lane => "gherkin";
        public IReadOnlyList<string> Modes { get; init; } = [RunnerWire.ColdMode];

        public IReadOnlyList<string> SpecIdentities { get; init; } =
            ["Orders/places an order", "Stock/counts"];

        public string? WarmUnavailable { get; set; }

        /// <summary>Held open so a test can have a run genuinely in flight.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public Exception? Throws { get; set; }

        /// <summary>Runs after a run completes, so a test can have one cost the suite its mode.</summary>
        public Action? AfterRun { get; set; }

        public async Task Run(string commandId, SpecSelection selection, string mode, CancellationToken token)
        {
            lock (Runs) Runs.Add((commandId, mode, selection.Identities));

            if (Gate is not null) await Gate.Task.WaitAsync(token);

            AfterRun?.Invoke();

            if (Throws is not null) throw Throws;
        }
    }

    private static ResidentRunnerOptions optionsFor(FakeMonitorHost host, string runnerId = "r1")
        => new()
        {
            Url = host.Url,
            RunnerId = runnerId,
            Repository = "/repo",
            Branch = "main",
            Backoff = TimeSpan.FromMilliseconds(20),
            MaxBackoff = TimeSpan.FromMilliseconds(80),
            RequestTimeout = TimeSpan.FromSeconds(2)
        };

    private static IReadOnlyList<CloudEvent> eventsOf(FakeMonitorHost host)
        => host.RunnerEvents.Select(json => CloudEvent.FromJson(json)!).ToList();

    private static async Task eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException(because);
    }

    private static CloudEvent command(string type, object data)
        => CloudEvent.From("stoat", type, data);

    // --- Registration.

    [Fact]
    public async Task registering_announces_the_lane_the_modes_and_every_specification()
    {
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        (await runner.Register()).ShouldBeTrue();

        var registration = eventsOf(host).ShouldHaveSingleItem();
        registration.Type.ShouldBe(RunnerWire.RegisteredType);
        registration.Source.ShouldBe("bobcat/runner/r1");

        var data = registration.DataAs<RunnerRegistration>().ShouldNotBeNull();
        data.RunnerId.ShouldBe("r1");
        data.Repository.ShouldBe("/repo");
        data.Branch.ShouldBe("main");
        data.Suite.ShouldBe("Orders.Specs");
        data.Lane.ShouldBe("gherkin");
        data.Modes.ShouldBe(["cold"]);
        data.Specs.ShouldBe(["Orders/places an order", "Stock/counts"]);
    }

    [Fact]
    public async Task warm_is_withdrawn_from_the_modes_once_the_suite_reports_it_damaged()
    {
        // Issue #393's rule, enforced here because the registration is where a monitor learns what
        // it may ask for. A runner that kept offering warm over a poisoned host would be inviting
        // a command it cannot honour.
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite { Modes = [RunnerWire.ColdMode, RunnerWire.WarmMode] };

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        runner.AvailableModes.ShouldBe(["cold", "warm"]);

        suite.WarmUnavailable = "the database reset threw";
        runner.AvailableModes.ShouldBe(["cold"], "cold is unaffected — it starts over anyway");
    }

    [Fact]
    public async Task a_withdrawn_warm_mode_is_refused_with_the_damage_that_withdrew_it()
    {
        // "warm is not a mode this runner offers", from a runner that was offering it a minute
        // ago, explains nothing. This is the one case where the generic message is not enough.
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite
        {
            Modes = [RunnerWire.ColdMode, RunnerWire.WarmMode],
            WarmUnavailable = "the database reset threw"
        };

        await using var runner = new ResidentRunner(suite, optionsFor(host));

        await runner.Handle(command(
            RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"], RunnerWire.WarmMode)));

        var ack = eventsOf(host).ShouldHaveSingleItem().DataAs<RunnerAcknowledgement>().ShouldNotBeNull();
        ack.Accepted.ShouldBeFalse();
        ack.Reason.ShouldContain("the database reset threw");
        ack.Refusal.ShouldBe(
            RunnerRefusal.UnsupportedMode,
            "a withdrawn mode is still a mode this runner does not offer");

        suite.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_run_that_cost_the_runner_a_mode_re_announces_itself()
    {
        // So the monitor stops offering a person a button that will now be refused. Registration
        // is idempotent, which is what makes a mid-session re-register safe.
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite { Modes = [RunnerWire.ColdMode, RunnerWire.WarmMode] };

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        (await runner.Register()).ShouldBeTrue();

        suite.AfterRun = () => suite.WarmUnavailable = "the database reset threw";

        await runner.Handle(command(
            RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"], RunnerWire.WarmMode)));

        await eventually(
            () => eventsOf(host).Count(e => e.Type == RunnerWire.RegisteredType) == 2,
            "the runner never re-registered after losing warm mode");

        eventsOf(host)
            .Where(e => e.Type == RunnerWire.RegisteredType)
            .Last()
            .DataAs<RunnerRegistration>()!
            .Modes.ShouldBe(["cold"]);
    }

    // --- The invariant: an absent or hostile monitor never matters.

    [Fact]
    public async Task no_monitor_means_no_registration_and_no_failure()
    {
        // The whole invariant in one line: a runner with nothing to talk to is an idle process,
        // not an incident.
        await using var runner = new ResidentRunner(
            new RecordingSuite(),
            new ResidentRunnerOptions
            {
                // A port nothing is listening on.
                Url = "http://127.0.0.1:1",
                RequestTimeout = TimeSpan.FromMilliseconds(400)
            });

        (await runner.Register()).ShouldBeFalse();
    }

    [Fact]
    public async Task an_absent_monitor_leaves_the_runner_waiting_rather_than_throwing()
    {
        await using var runner = new ResidentRunner(
            new RecordingSuite(),
            new ResidentRunnerOptions
            {
                Url = "http://127.0.0.1:1",
                Backoff = TimeSpan.FromMilliseconds(10),
                MaxBackoff = TimeSpan.FromMilliseconds(30),
                RequestTimeout = TimeSpan.FromMilliseconds(200)
            });

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));

        // Returns, never throws — a resident runner that died of a blip would be worse than one
        // quietly waiting, because its parent only relaunches it on a source change.
        await runner.Run(stop.Token);
    }

    [Fact]
    public async Task a_monitor_that_refuses_the_stream_is_retried_rather_than_fatal()
    {
        using var host = new FakeMonitorHost { RefuseCommandStream = true };

        await using var runner = new ResidentRunner(new RecordingSuite(), optionsFor(host));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await runner.Run(stop.Token);

        // It kept asking: a monitor that does not know this runner yet may learn.
        host.RunnerEvents.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task a_dropped_stream_reconnects_and_resumes_from_the_last_event_id()
    {
        using var host = new FakeMonitorHost { CloseCommandStreamAfter = 1 };
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        using var stop = new CancellationTokenSource();

        var loop = runner.Run(stop.Token);

        host.Send("42", command(RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"])));

        await eventually(() => host.LastEventIds.Count >= 2, "the runner never reconnected");

        await stop.CancelAsync();
        await loop;

        host.LastEventIds[0].ShouldBeNull("the first connection has nothing to resume from");
        host.LastEventIds[1].ShouldBe("42", "a reconnect resumes where the stream left off");
    }

    [Fact]
    public async Task a_command_that_is_not_a_cloudevent_is_ignored_rather_than_fatal()
    {
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        using var stop = new CancellationTokenSource();
        var loop = runner.Run(stop.Token);

        host.SendRaw("id: 1\nevent: stoat.runner.command.run\ndata: <html>not json</html>\n\n");
        host.Send("2", command(RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"])));

        await eventually(() => suite.Runs.Count == 1, "the good command never ran");

        await stop.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task a_command_type_the_runner_does_not_know_is_ignored()
    {
        // So a monitor can put new things on this stream without a runner needing to be upgraded
        // in lockstep.
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));

        await runner.Handle(command("stoat.runner.command.something.new", new { commandId = "c9" }));

        suite.Runs.ShouldBeEmpty();
        host.RunnerEvents.ShouldBeEmpty("nothing to acknowledge, because nothing was understood");
    }

    [Fact]
    public async Task a_keepalive_is_skipped_without_being_treated_as_a_command()
    {
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        using var stop = new CancellationTokenSource();
        var loop = runner.Run(stop.Token);

        host.SendKeepalive("1");
        host.Send("2", command(RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"])));

        await eventually(() => suite.Runs.Count == 1, "the command after the keepalive never ran");

        await stop.CancelAsync();
        await loop;
    }

    // --- Running a command.

    [Fact]
    public async Task an_accepted_command_is_acknowledged_and_then_run()
    {
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));

        await runner.Handle(command(
            RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"], RunnerWire.ColdMode)));

        await eventually(() => suite.Runs.Count == 1, "the command never ran");

        var ack = eventsOf(host).ShouldHaveSingleItem();
        ack.Type.ShouldBe(RunnerWire.AcknowledgedType);

        var data = ack.DataAs<RunnerAcknowledgement>().ShouldNotBeNull();
        data.CommandId.ShouldBe("c1");
        data.Accepted.ShouldBeTrue();
        data.Reason.ShouldBeNull();

        var run = suite.Runs.ShouldHaveSingleItem();
        run.CommandId.ShouldBe("c1");
        run.Mode.ShouldBe("cold");
        run.Specs.ShouldBe(["Stock/counts"]);
    }

    [Fact]
    public async Task a_command_with_no_mode_runs_cold()
    {
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        await runner.Handle(command(RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"])));

        await eventually(() => suite.Runs.Count == 1, "the command never ran");
        suite.Runs[0].Mode.ShouldBe("cold");
    }

    [Fact]
    public async Task a_suite_that_cannot_be_run_at_all_is_reported_and_does_not_end_the_runner()
    {
        var logged = new List<string>();
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite { Throws = new InvalidOperationException("the broker never came up") };

        await using var runner = new ResidentRunner(
            suite, optionsFor(host) with { Log = logged.Add });

        await runner.Handle(command(RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"])));
        await eventually(() => !runner.Busy, "the run never finished");

        logged.ShouldContain(line => line.Contains("the broker never came up"));

        // And the next command is still taken.
        suite.Throws = null;
        await runner.Handle(command(RunnerWire.RunCommandType, new RunCommand("c2", ["Stock/counts"])));
        await eventually(() => suite.Runs.Count == 2, "the runner stopped taking commands");
    }

    // --- The four refusals. Each carries the word a monitor acts on (issue #400) beside the
    //     sentence a person reads, because only one of the four — busy — means "send it again".

    [Fact]
    public async Task a_specification_outside_this_runners_set_is_refused_by_name()
    {
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));

        await runner.Handle(command(
            RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts", "Nope/not here"])));

        var ack = eventsOf(host).ShouldHaveSingleItem().DataAs<RunnerAcknowledgement>().ShouldNotBeNull();
        ack.Accepted.ShouldBeFalse();
        ack.Reason.ShouldContain("'Nope/not here'");
        ack.Refusal.ShouldBe(RunnerRefusal.UnknownSpec);

        suite.Runs.ShouldBeEmpty("not even the identity it did have");
    }

    [Fact]
    public async Task a_second_command_while_busy_is_refused_rather_than_queued()
    {
        // The monitor owns the queue. A runner that silently queued would leave a person waiting
        // on a run whose turn they cannot see.
        using var host = new FakeMonitorHost();
        var gate = new TaskCompletionSource();
        var suite = new RecordingSuite { Gate = gate };

        await using var runner = new ResidentRunner(suite, optionsFor(host));

        await runner.Handle(command(RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"])));
        await eventually(() => runner.Busy, "the first command never started");

        await runner.Handle(command(RunnerWire.RunCommandType, new RunCommand("c2", ["Stock/counts"])));

        var second = eventsOf(host)
            .Select(e => e.DataAs<RunnerAcknowledgement>()!)
            .Single(a => a.CommandId == "c2");

        second.Accepted.ShouldBeFalse();
        second.Reason.ShouldContain("already running");

        // The one refusal a monitor answers by sending again — and the only reason this field
        // exists, since Stoat was matching on the sentence to tell it apart.
        second.Refusal.ShouldBe(RunnerRefusal.Busy);

        gate.SetResult();
        await eventually(() => !runner.Busy, "the first command never finished");
        suite.Runs.Select(r => r.CommandId).ShouldBe(["c1"]);
    }

    [Fact]
    public async Task a_mode_this_runner_did_not_register_is_refused_and_never_downgraded()
    {
        // A person who asked for warm and silently got cold would read the resulting wall clock as
        // warm mode not working.
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));

        await runner.Handle(command(
            RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"], RunnerWire.WarmMode)));

        var ack = eventsOf(host).ShouldHaveSingleItem().DataAs<RunnerAcknowledgement>().ShouldNotBeNull();
        ack.Accepted.ShouldBeFalse();
        ack.Reason.ShouldContain("'warm' is not a mode this runner offers");
        ack.Refusal.ShouldBe(RunnerRefusal.UnsupportedMode);

        suite.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_command_naming_no_specification_is_refused()
    {
        // Far more likely a mistake on the asking side than a request for the whole suite — and
        // the whole suite is what an ordinary run already does.
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        await runner.Handle(command(RunnerWire.RunCommandType, new RunCommand("c1", [])));

        var empty = eventsOf(host).ShouldHaveSingleItem().DataAs<RunnerAcknowledgement>().ShouldNotBeNull();
        empty.Reason.ShouldContain("at least one specification");
        empty.Refusal.ShouldBe(RunnerRefusal.Empty);

        suite.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_cold_command_is_still_taken_after_warm_has_been_withdrawn()
    {
        // Because cold is what starting over means: whatever poisoned the warm host is exactly
        // what a cold run starts from.
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite
        {
            Modes = [RunnerWire.ColdMode, RunnerWire.WarmMode],
            WarmUnavailable = "the database reset threw"
        };

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        await runner.Handle(command(RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"])));

        await eventually(() => suite.Runs.Count == 1, "the cold command was refused too");
        eventsOf(host).ShouldHaveSingleItem().DataAs<RunnerAcknowledgement>()!.Accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task a_run_command_with_no_command_id_is_ignored_because_nothing_could_be_answered()
    {
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        await runner.Handle(command(RunnerWire.RunCommandType, new { specs = new[] { "Stock/counts" } }));

        host.RunnerEvents.ShouldBeEmpty();
        suite.Runs.ShouldBeEmpty();
    }

    // --- Restart.

    [Fact]
    public async Task restart_is_acknowledged_and_then_the_runner_stops()
    {
        using var host = new FakeMonitorHost();
        var suite = new RecordingSuite();

        await using var runner = new ResidentRunner(suite, optionsFor(host));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var loop = runner.Run(stop.Token);

        host.Send("1", command(RunnerWire.RestartCommandType, new RestartCommand("c1")));

        await loop;

        runner.RestartRequested.ShouldBeTrue();

        var ack = eventsOf(host)
            .Select(e => e.DataAs<RunnerAcknowledgement>())
            .First(a => a?.CommandId == "c1")!;

        ack.Accepted.ShouldBeTrue("the runner is going to do exactly what was asked");
    }

    [Fact]
    public async Task restart_cuts_a_run_short_rather_than_waiting_for_it()
    {
        // A wedged run is the main reason someone restarts a runner, so a restart that waited
        // would be useless in exactly the case it exists for.
        using var host = new FakeMonitorHost();
        var gate = new TaskCompletionSource();
        var suite = new RecordingSuite { Gate = gate };

        await using var runner = new ResidentRunner(suite, optionsFor(host));

        await runner.Handle(command(RunnerWire.RunCommandType, new RunCommand("c1", ["Stock/counts"])));
        await eventually(() => runner.Busy, "the run never started");

        await runner.Handle(command(RunnerWire.RestartCommandType, new RestartCommand("c2")));

        await eventually(() => !runner.Busy, "the in-flight run was waited on instead of cut short");
        runner.RestartRequested.ShouldBeTrue();
    }
}
