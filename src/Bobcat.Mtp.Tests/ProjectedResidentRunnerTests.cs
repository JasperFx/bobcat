using System.Diagnostics;
using System.Text.Json;
using Bobcat.Residency;
using Bobcat.Supervisor;
using Bobcat.Runtime;
using Bobcat.Tests.Monitoring;
using Shouldly;

namespace Bobcat.Mtp.Tests;

/// <summary>
/// Issue #399: a resident runner for a suite whose process Bobcat does not own. The subject is
/// <c>Bobcat.Xunit.Samples</c> — a real xUnit v3 host carrying <c>Bobcat.Xunit</c> — driven by a
/// runner that is a different process, over real HTTP, with a stand-in console at the far end.
/// </summary>
/// <remarks>
/// <para>
/// Pinned from this project because it is the one that already launches test hosts as processes,
/// and because the claim under test is a claim about <i>two</i> lanes answering one kind of
/// request: the same <c>SpecIdentity</c> a monitor sends to the Gherkin runner, sent to this one.
/// </para>
/// <para>
/// The runner is exercised at both altitudes on purpose. The <c>OutOfProcessResidentSuite</c>
/// tests drive the mechanism — listing, translation, the child launch and what it inherits — and
/// the <c>bobcat resident</c> test drives the executable a parent actually launches, because a
/// class nothing ships is not a feature.
/// </para>
/// </remarks>
public class ProjectedResidentRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "bobcat-projected-resident", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* a temp directory is not worth failing a test over */ }
    }

    private static string binaryIn(string project, string executable)
    {
        var configuration = Path.GetFileName(Path.GetDirectoryName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, project, "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? executable + ".exe" : executable);
    }

    private static readonly string projectedHost =
        binaryIn("Bobcat.Xunit.Samples", "Bobcat.Xunit.Samples");

    /// <summary>
    /// The <c>bobcat</c> tool. Its assembly is <c>Bobcat.Cli</c> and not <c>bobcat</c>, because the
    /// tool references core and <c>bobcat.dll</c> beside <c>Bobcat.dll</c> is one file on a
    /// case-insensitive filesystem.
    /// </summary>
    private static readonly string tool = binaryIn("Bobcat.Console", "Bobcat.Cli");

    private const string knownSpec = "Calculator/using sentences";

    private const string secondSpec = "Facts/facts in action";

    private async Task<OutOfProcessResidentSuite> suiteFor(FakeMonitorHost monitor)
        => await OutOfProcessResidentSuite.For(
            projectedHost, monitorUrl: monitor.Url, listingDirectory: _directory);

    private async Task<WarmProjectedResidentSuite> warmSuiteFor(FakeMonitorHost monitor)
        => await WarmProjectedResidentSuite.For(
            projectedHost, monitorUrl: monitor.Url, listingDirectory: _directory);

    /// <summary>
    /// Hand the runner a run command, waiting first until it is free.
    /// </summary>
    /// <remarks>
    /// <b><c>run_finished</c> does not mean the runner is free.</b> The run bracket closes INSIDE
    /// the run, a hair before the in-flight slot clears, so a command sent the instant
    /// <c>run_finished</c> arrives is legitimately refused as busy — and busy means "not now",
    /// not "not this", so a client that wants a second run sends again. Writing this test without
    /// the wait reproduced exactly that: <c>accepted=False refusal=busy</c> on the second command.
    /// Invisible on a slow machine and reliable on a fast one, which is the wrong way round.
    /// </remarks>
    private static async Task handleWhenFree(ResidentRunner runner, string commandId, RunCommand command)
    {
        await eventually(() => runner.Busy ? null : "free", "the runner never became free");
        await runner.Handle(CloudEvent.From("stoat", RunnerWire.RunCommandType, command));
    }

    private static async Task<T> eventually<T>(Func<T?> read, string because) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (read() is { } value) return value;
            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException(because);
    }

    private static IEnumerable<CloudEvent> eventsOf(FakeMonitorHost monitor)
        => monitor.RunnerEvents.Select(json => CloudEvent.FromJson(json)!);

    private static T? payload<T>(FakeMonitorHost monitor, string type) where T : class
        => eventsOf(monitor).Where(e => e.Type == type).Select(e => e.DataAs<T>()).LastOrDefault();

    private static IReadOnlyList<JsonElement> eventsNamed(FakeMonitorHost monitor, string name)
    {
        var found = new List<JsonElement>();

        foreach (var batch in monitor.Batches)
        {
            using var document = JsonDocument.Parse(batch);
            var root = document.RootElement;

            var events = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : root.TryGetProperty("events", out var nested) && nested.ValueKind == JsonValueKind.Array
                    ? nested.EnumerateArray()
                    : default;

            foreach (var @event in events)
            {
                if (@event.TryGetProperty("type", out var type) && type.GetString() == name)
                {
                    found.Add(@event.Clone());
                }
            }
        }

        return found;
    }

    // --- The suite: what it says it is, and what a command does to it.

    [Fact]
    public async Task a_projected_suite_registers_its_identities_and_offers_cold()
    {
        using var monitor = new FakeMonitorHost();
        var suite = await suiteFor(monitor);

        suite.Suite.ShouldBe("Bobcat.Xunit.Samples");
        suite.Lane.ShouldBe(SpecManifest.ProjectedLane);
        suite.SpecIdentities.ShouldContain(knownSpec);

        // The in-core suite is the cold one and stays so: warmth means holding a live MTP client,
        // which lives in Bobcat.Supervisor because core is what every spec project references.
        // WarmProjectedResidentSuite is what offers both; see the warm tests below.
        suite.Modes.ShouldBe([RunnerWire.ColdMode]);
    }

    [Fact]
    public async Task the_warm_capable_suite_offers_both_modes()
    {
        using var monitor = new FakeMonitorHost();
        await using var suite = await warmSuiteFor(monitor);

        suite.Suite.ShouldBe("Bobcat.Xunit.Samples");
        suite.Lane.ShouldBe(SpecManifest.ProjectedLane);
        suite.Modes.ShouldBe([RunnerWire.ColdMode, RunnerWire.WarmMode]);

        // Every launch-time refusal the cold suite makes still happens, and still at launch:
        // this delegates construction to it rather than reimplementing the checks.
        suite.SpecIdentities.ShouldContain(knownSpec);
    }

    [Fact]
    public async Task two_warm_commands_are_two_runs_in_one_live_host()
    {
        // #402 item 4, end to end against the real xUnit host. The two halves this proves
        // together are what the issue is about: the host STAYS UP across commands (warmth), and
        // each command is nonetheless its own run on the wire (#393's requirement, which the
        // per-request run bracket is what makes possible).
        using var monitor = new FakeMonitorHost();
        await using var suite = await warmSuiteFor(monitor);

        await using var runner = new ResidentRunner(suite, new ResidentRunnerOptions
        {
            Url = monitor.Url,
            RunnerId = "warm-projected-runner"
        });

        (await runner.Register()).ShouldBeTrue();

        payload<RunnerRegistration>(monitor, RunnerWire.RegisteredType).ShouldNotBeNull()
            .Modes.ShouldContain(RunnerWire.WarmMode);

        await handleWhenFree(runner, "c-warm-1", new RunCommand("c-warm-1", [knownSpec], RunnerWire.WarmMode));

        await eventually(
            () => eventsNamed(monitor, "run_finished").Count >= 1 ? "yes" : null,
            "the first warm command never closed its run");

        await handleWhenFree(runner, "c-warm-2", new RunCommand("c-warm-2", [secondSpec], RunnerWire.WarmMode));

        await eventually(
            () => eventsNamed(monitor, "run_finished").Count >= 2 ? "yes" : null,
            "the second warm command never closed its run");

        var started = eventsNamed(monitor, "run_started");
        started.Count.ShouldBe(2, "each command is its own run");

        started.Select(e => e.GetProperty("runId").GetString()).Distinct().Count()
            .ShouldBe(2, "and its own RunId — a shared one collapses the two cards into one");

        // THE per-request command id, which is the thing a warm child cannot be told through its
        // environment: it is fixed at launch, so the first command's id would have been stamped on
        // both. BOBCAT_RUN_COMMAND_FILE is the channel, rewritten before each request.
        started.Select(e => e.GetProperty("command").GetString())
            .ShouldBe(["c-warm-1", "c-warm-2"]);

        // And each command ran its own specification, not the whole suite.
        eventsNamed(monitor, "scenario_finished")
            .Select(e => e.GetProperty("uid").GetString())
            .ShouldBe([knownSpec, secondSpec]);
    }

    [Fact]
    public async Task a_warm_command_naming_an_identity_the_suite_does_not_have_is_refused_by_name()
    {
        using var monitor = new FakeMonitorHost();
        await using var suite = await warmSuiteFor(monitor);

        await using var runner = new ResidentRunner(suite, new ResidentRunnerOptions
        {
            Url = monitor.Url,
            RunnerId = "warm-refusal-runner"
        });

        (await runner.Register()).ShouldBeTrue();

        await runner.Handle(CloudEvent.From(
            "stoat", RunnerWire.RunCommandType,
            new RunCommand("c-foreign", ["Nothing/at all"], RunnerWire.WarmMode)));

        var acknowledgement = await eventually(
            () => payload<RunnerAcknowledgement>(monitor, RunnerWire.AcknowledgedType) is { Accepted: false } no
                ? no
                : null,
            "the runner never refused the foreign identity");

        acknowledgement.Reason.ShouldContain("Nothing/at all");
        acknowledgement.Refusal.ShouldBe(RunnerRefusal.UnknownSpec);

        // Refused BEFORE anything ran, which is the whole point of refusing by name: a narrowed
        // run that matched nothing exits 0 and looks like a pass.
        eventsNamed(monitor, "run_started").ShouldBeEmpty();
    }

    [Fact]
    public async Task a_cold_command_closes_the_warm_session_first()
    {
        // They cannot coexist for the same reason they cannot in the Gherkin lane: the booted host
        // holds the port, the database and the queues a second one would ask for, so "fresh
        // everything" has to include tearing down what is up.
        using var monitor = new FakeMonitorHost();
        await using var suite = await warmSuiteFor(monitor);

        await suite.Run("c-warm", SpecSelection.Of(knownSpec), RunnerWire.WarmMode, default);
        await suite.Run("c-cold", SpecSelection.Of(knownSpec), RunnerWire.ColdMode, default);

        // Two runs, two ids, and the cold one is a fresh process — which is observable only in
        // that it published its own bracket like any other run.
        await eventually(
            () => eventsNamed(monitor, "run_finished").Count >= 2 ? "yes" : null,
            "both commands should have closed their runs");

        eventsNamed(monitor, "run_started")
            .Select(e => e.GetProperty("command").GetString())
            .ShouldBe(["c-warm", "c-cold"]);
    }

    [Fact]
    public async Task a_commanded_run_launches_the_suites_own_host_and_publishes_as_the_command()
    {
        using var monitor = new FakeMonitorHost();
        var suite = await suiteFor(monitor);

        await using var runner = new ResidentRunner(suite, new ResidentRunnerOptions
        {
            Url = monitor.Url,
            RunnerId = "projected-runner"
        });

        (await runner.Register()).ShouldBeTrue();

        var registration = payload<RunnerRegistration>(monitor, RunnerWire.RegisteredType).ShouldNotBeNull();
        registration.Lane.ShouldBe(SpecManifest.ProjectedLane);
        registration.Specs.ShouldContain(knownSpec);

        await runner.Handle(CloudEvent.From(
            "stoat", RunnerWire.RunCommandType, new RunCommand("c-projected", [knownSpec])));

        var runs = await eventually(
            () => eventsNamed(monitor, "run_started") is { Count: 1 } one ? one : null,
            "the child host never published run_started");

        // The command travels through BOBCAT_RUN_COMMAND — which is the case that variable was
        // built for (issue #392), since the runner and the run are different processes here.
        runs[0].GetProperty("command").GetString().ShouldBe("c-projected");
        runs[0].GetProperty("suite").GetString().ShouldBe("Bobcat.Xunit.Samples");

        // And not the session that launched the runner (issue #401) — the rule rides along into
        // the child for free, because it is keyed on the command rather than on a flag.
        runs[0].GetProperty("session").ValueKind.ShouldBe(JsonValueKind.Null);

        // One specification, not the whole suite. A framework that did not understand the filter
        // would run all 41 and look exactly like a filtered run that matched everything.
        await eventually(
            () => eventsNamed(monitor, "scenario_finished").Count >= 1 ? "yes" : null,
            "the child host published no scenario");

        eventsNamed(monitor, "scenario_finished")
            .Select(e => e.GetProperty("uid").GetString())
            .ShouldBe([knownSpec]);
    }

    [Fact]
    public async Task a_foreign_specification_is_refused_before_any_process_is_launched()
    {
        using var monitor = new FakeMonitorHost();
        var suite = await suiteFor(monitor);

        await using var runner = new ResidentRunner(suite, new ResidentRunnerOptions { Url = monitor.Url });

        await runner.Handle(CloudEvent.From(
            "stoat", RunnerWire.RunCommandType, new RunCommand("c-foreign", ["Nope/not here"])));

        var ack = eventsOf(monitor)
            .Select(e => e.DataAs<RunnerAcknowledgement>())
            .Single(a => a?.CommandId == "c-foreign")!;

        ack.Accepted.ShouldBeFalse();
        ack.Refusal.ShouldBe(RunnerRefusal.UnknownSpec);
        eventsNamed(monitor, "run_started").ShouldBeEmpty("nothing should have been launched");
    }

    [Fact]
    public async Task warm_is_refused_rather_than_silently_run_cold()
    {
        using var monitor = new FakeMonitorHost();
        var suite = await suiteFor(monitor);

        await using var runner = new ResidentRunner(suite, new ResidentRunnerOptions { Url = monitor.Url });

        await runner.Handle(CloudEvent.From(
            "stoat",
            RunnerWire.RunCommandType,
            new RunCommand("c-warm", [knownSpec], RunnerWire.WarmMode)));

        var ack = eventsOf(monitor)
            .Select(e => e.DataAs<RunnerAcknowledgement>())
            .Single(a => a?.CommandId == "c-warm")!;

        ack.Accepted.ShouldBeFalse();
        ack.Refusal.ShouldBe(RunnerRefusal.UnsupportedMode);
    }

    // --- What the child is handed, which is where the quiet breakages live.

    [Fact]
    public void the_child_never_inherits_the_variables_that_would_break_its_run()
    {
        var environment = OutOfProcessResidentSuite.EnvironmentFor("cmd-1");

        // Every one of these is inherited by default from a parent that set it for its own
        // reasons, and every one of them breaks the run silently rather than loudly.
        environment["BOBCAT_RUN_ID"].ShouldBeNull("or every command publishes under one run id");
        environment["BOBCAT_RUN_OWNER"].ShouldBeNull("or no child publishes a run bracket at all");
        environment[SpecManifest.PathVariable].ShouldBeNull("a run is not a listing");
        environment[ResidentMode.Variable].ShouldBeNull("or the child registers instead of running");

        environment["BOBCAT_RUN_COMMAND"].ShouldBe("cmd-1");
    }

    [Fact]
    public async Task an_unbuilt_host_is_refused_at_launch_rather_than_registered_and_useless()
    {
        var missing = Path.Combine(_directory, "NotBuilt");

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => OutOfProcessResidentSuite.For(missing, listingDirectory: _directory));

        failure.Message.ShouldContain("There is no test host at");
    }

    // --- The executable a parent launches.

    [Fact]
    public async Task the_bobcat_tool_runs_a_projected_suite_resident_and_exits_75_on_restart()
    {
        File.Exists(tool).ShouldBeTrue($"The bobcat tool was not built at {tool}");

        using var monitor = new FakeMonitorHost();

        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(tool)!
        };

        info.ArgumentList.Add("resident");
        info.ArgumentList.Add(projectedHost);
        info.ArgumentList.Add("--url");
        info.ArgumentList.Add(monitor.Url);

        // Issue #397's half of this: the parent names the runner, so the same checkout is the same
        // runner across every relaunch.
        info.Environment["BOBCAT_RUNNER_ID"] = "the-projected-checkout";
        info.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";

        var output = new List<string>();
        using var process = Process.Start(info)!;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add("ERR " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            var registration = await eventually(
                () => payload<RunnerRegistration>(monitor, RunnerWire.RegisteredType),
                "the tool never registered a projected runner");

            registration.RunnerId.ShouldBe("the-projected-checkout");
            registration.Suite.ShouldBe("Bobcat.Xunit.Samples");
            registration.Lane.ShouldBe(SpecManifest.ProjectedLane);

            // The tool offers both now (issue #402 item 4). It used to be cold alone, and the
            // blocker was Bobcat's own run bracket rather than the platform — closing that is what
            // let the warm mode be registered honestly rather than registered and then refused.
            registration.Modes.ShouldBe([RunnerWire.ColdMode, RunnerWire.WarmMode]);
            registration.Specs.ShouldContain(knownSpec);

            monitor.Send("1", CloudEvent.From(
                "stoat", RunnerWire.RestartCommandType, new RestartCommand("t-restart")));

            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

            string said;
            lock (output) said = string.Join("\n  ", output);

            process.ExitCode.ShouldBe(
                ResidentMode.RestartExitCode,
                "a restart is 'relaunch me', which 0 cannot say. the tool said:\n  " + said);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }
}
