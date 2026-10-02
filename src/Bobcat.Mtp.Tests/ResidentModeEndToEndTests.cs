using System.Diagnostics;
using System.Text.Json;
using Bobcat.Residency;
using Bobcat.Tests.Monitoring;
using Shouldly;

namespace Bobcat.Mtp.Tests;

/// <summary>
/// Issue #390 end to end: a real Bobcat spec host, launched as <c>--resident</c>, registering with
/// a monitor over HTTP and running a specification because the monitor asked.
/// </summary>
/// <remarks>
/// <para>
/// This is as close as this repository can get to the issue's "against a live Stoat": the runner
/// is the real process started through the real generated entry point, the transport is real
/// HTTP, and the only stand-in is the console at the other end. What it proves that
/// <c>ResidentRunnerTests</c> cannot is that <c>--resident</c> reaches the runner at all — the
/// generated <c>Main</c> calls <c>BobcatTestApplication.Run</c>, which has to decide not to become
/// a test host before it builds one.
/// </para>
/// <para>
/// <c>Bobcat.Mtp.GeneratedHost</c> is the subject because it has no hand-written <c>Main</c>
/// anywhere, so resident mode is reached through exactly the path a consumer gets for free.
/// </para>
/// </remarks>
public class ResidentModeEndToEndTests
{
    private static readonly string hostPath = locateHost();

    /// <summary>The issue #398 subject: a host whose Main is <c>BobcatRunner.Run</c>.</summary>
    private static readonly string previewPath = locate("ConsolePreview");

    private static string locateHost() => locate("Bobcat.Mtp.GeneratedHost");

    private static string locate(string project)
    {
        var configuration = Path.GetFileName(Path.GetDirectoryName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, project, "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? project + ".exe" : project);
    }

    private static Process launch(
        FakeMonitorHost monitor, IReadOnlyDictionary<string, string>? environment = null)
    {
        File.Exists(hostPath).ShouldBeTrue($"The generated host was not built at {hostPath}");

        var info = new ProcessStartInfo(hostPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!
        };

        info.ArgumentList.Add(ResidentMode.Option);
        info.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["BOBCAT_MONITOR_URL"] = monitor.Url;

        // Said explicitly, because a test that wants the wire must not depend on the ambient
        // value: CI sets BOBCAT_MONITOR=0 for the whole job (so that spec hosts in the suite do
        // not probe 5525), the child inherits it, MonitorPublisher.Disabled short-circuits before
        // the probe, and this reads as "the commanded run never published run_started" — the
        // feature working perfectly and the test asserting against a publisher that was switched
        // off. Three tests failed that way on the v0.29.0 tag.
        info.Environment["BOBCAT_MONITOR"] = "1";

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[name] = value;
        }

        var process = Process.Start(info)!;

        // Kept so a failure can say what the runner itself thought was happening. Without it a
        // timed-out poll says only "nothing arrived", which is the same sentence for a refused
        // command, an absent publisher and a runner that never registered.
        _output.Clear();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.Add("ERR " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static readonly List<string> _output = new();

    private static string log()
    {
        lock (_output) return string.Join("\n  ", _output);
    }

    private static async Task<T> eventually<T>(Func<T?> read, string because) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (read() is { } value) return value;
            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException(because);
    }

    private static async Task<T> eventuallyValue<T>(Func<T?> read, string because) where T : struct
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (read() is { } value) return value;
            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException(because);
    }

    /// <summary>
    /// Send a run command, and send it again while the runner answers "busy" — which is what a
    /// monitor does, because the monitor owns the queue and the runner takes one at a time.
    /// </summary>
    private static async Task sendUntilAccepted(FakeMonitorHost monitor, string id, RunCommand command)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            monitor.Send(id, CloudEvent.From("stoat", RunnerWire.RunCommandType, command));

            var answered = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < answered)
            {
                var answers = eventsOf(monitor)
                    .Select(e => e.DataAs<RunnerAcknowledgement>())
                    .Where(a => a?.CommandId == command.CommandId)
                    .ToList();

                // ANY acceptance is the answer, not the latest one: a resend that races an
                // already-accepted command is itself refused as busy, and reading only the last
                // answer would spin until the deadline over a command that is running fine.
                if (answers.Any(a => a!.Accepted)) return;
                if (answers.Count > 0) break;

                await Task.Delay(50);
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException(
            $"'{command.CommandId}' was never accepted. the runner said:\n  " + log());
    }

    private static IEnumerable<CloudEvent> eventsOf(FakeMonitorHost monitor)
        => monitor.RunnerEvents.Select(json => CloudEvent.FromJson(json)!);

    private static T? payload<T>(FakeMonitorHost monitor, string type) where T : class
        => eventsOf(monitor).Where(e => e.Type == type).Select(e => e.DataAs<T>()).LastOrDefault();

    [Fact]
    public async Task a_real_host_registers_runs_a_named_specification_and_restarts_on_command()
    {
        using var monitor = new FakeMonitorHost();
        using var process = launch(monitor, new Dictionary<string, string>
        {
            // Issue #397: a parent hands the runner its id, so the same checkout is the same
            // runner across every rebuild.
            ["BOBCAT_RUNNER_ID"] = "runner-for-this-checkout",

            // Issue #401: the runner is launched from an agent's terminal, which is how this
            // variable gets into a resident runner in the first place. Everything it runs from
            // here is for somebody else's button press.
            ["CLAUDE_CODE_SESSION_ID"] = "the-session-that-launched-the-runner"
        });

        try
        {
            // 1. Register. It arrives without anything asking for it: the runner is the client.
            var registration = await eventually(
                () => payload<RunnerRegistration>(monitor, RunnerWire.RegisteredType),
                "the host never registered");

            registration.RunnerId.ShouldBe(
                "runner-for-this-checkout",
                "the runner registers under the id its parent handed it, not a minted one");

            registration.Suite.ShouldBe("Bobcat.Mtp.GeneratedHost");
            registration.Lane.ShouldBe("gherkin");
            registration.Modes.ShouldBe([RunnerWire.ColdMode, RunnerWire.WarmMode]);
            registration.Specs.ShouldBe([
                "Ordering/An order can be emptied",
                "Ordering/An order is accepted",
                "Shipping/A shipment is labelled"
            ]);

            // 2. A command for a specification it does not have is refused by name, and the
            //    refusal is the message a person reads — not silence, which looks like a dead
            //    runner.
            monitor.Send("1", CloudEvent.From(
                "stoat", RunnerWire.RunCommandType, new RunCommand("c-foreign", ["Nope/not here"])));

            var refusal = await eventually(
                () => eventsOf(monitor)
                    .Select(e => e.DataAs<RunnerAcknowledgement>())
                    .FirstOrDefault(a => a?.CommandId == "c-foreign"),
                "the foreign specification was never acknowledged");

            refusal.Accepted.ShouldBeFalse();
            refusal.Reason.ShouldContain("'Nope/not here'");

            // And the machine's half of the same answer (issue #400), so a monitor owning the
            // queue knows this one is "not this" rather than "not now" without reading the prose.
            refusal.Refusal.ShouldBe(RunnerRefusal.UnknownSpec);

            // 3. A command it does have is accepted and run — and the run's events reach the
            //    ingest stream carrying the command that caused it (issue #392).
            monitor.Send("2", CloudEvent.From(
                "stoat",
                RunnerWire.RunCommandType,
                new RunCommand("c-good", ["Ordering/An order is accepted"])));

            var accepted = await eventually(
                () => eventsOf(monitor)
                    .Select(e => e.DataAs<RunnerAcknowledgement>())
                    .FirstOrDefault(a => a?.CommandId == "c-good"),
                "the command was never acknowledged");

            accepted.Accepted.ShouldBeTrue(accepted.Reason);
            accepted.Refusal.ShouldBeNull("an acceptance refuses nothing");

            var started = await eventuallyValue(
                () => runStarted(monitor),
                "the commanded run never published run_started. the runner said:\n  " + log());

            started.GetProperty("command").GetString().ShouldBe("c-good");
            started.GetProperty("mode").GetString().ShouldBe(BobcatResidentSuite.Mode);
            started.GetProperty("totalScenarios").GetInt32()
                .ShouldBe(1, "the run was narrowed to the one specification the command named");

            // ...and NOT the session that launched the runner (issue #401). The variable is in
            // this process's environment — it is how an agent's terminal marks everything it
            // starts — but a person pressed this button, and `command` above is the true answer
            // to who asked.
            started.GetProperty("session").ValueKind.ShouldBe(
                JsonValueKind.Null,
                "a commanded run is not attributed to the session that launched the runner");

            // 4. Restart means exit, so a parent can relaunch. Exit code 75 (EX_TEMPFAIL), which
            //    still says nothing about any test (issue #397) but does say "relaunch me" — a
            //    thing 0 could not say, because 0 is what a non-resident host returns after
            //    running its whole suite.
            monitor.Send("3", CloudEvent.From(
                "stoat", RunnerWire.RestartCommandType, new RestartCommand("c-restart")));

            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            process.ExitCode.ShouldBe(ResidentMode.RestartExitCode);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }

    /// <summary>The first <c>run_started</c> out of whatever batches reached the ingest route.</summary>
    private static JsonElement? runStarted(FakeMonitorHost monitor)
    {
        var all = runsStarted(monitor);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>Every <c>run_started</c>, in arrival order.</summary>
    private static IReadOnlyList<JsonElement> runsStarted(FakeMonitorHost monitor)
        => eventsNamed(monitor, "run_started");

    /// <summary>Every <c>run_finished</c>, in arrival order.</summary>
    private static IReadOnlyList<JsonElement> runsFinished(FakeMonitorHost monitor)
        => eventsNamed(monitor, "run_finished");

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

    [Fact]
    public async Task two_warm_commands_are_two_runs_on_the_wire_each_naming_its_own_command()
    {
        // Issue #393's wire rule: a viewer cannot tell a warm run from a cold one except by its
        // speed, because each command opens and closes its own run_started … run_finished bracket.
        // Checked against the real host because the bracket is attached per selection inside
        // BobcatRunner, and a session that published once would look like one very long run.
        using var monitor = new FakeMonitorHost();
        using var process = launch(monitor);

        try
        {
            var registration = await eventually(
                () => payload<RunnerRegistration>(monitor, RunnerWire.RegisteredType),
                "the host never registered");

            registration.Modes.ShouldBe(
                [RunnerWire.ColdMode, RunnerWire.WarmMode],
                "a Gherkin suite can keep its own host booted, so it offers warm");

            monitor.Send("1", CloudEvent.From(
                "stoat",
                RunnerWire.RunCommandType,
                new RunCommand("w-1", ["Ordering/An order is accepted"], RunnerWire.WarmMode)));

            // Waited on run_FINISHED, not run_started, and the difference is the whole race this
            // test first had. run_started is the beginning of the first run, so sending the second
            // command there finds the runner still busy — and a busy runner REFUSES, by design
            // (the monitor owns the queue). Six runs in eight failed that way, with the product
            // behaving exactly as specified and the test asking for the impossible.
            //
            // Waiting for the close also pins half of #393's claim on the way past: a warm
            // command's run opens AND closes its own bracket.
            await eventuallyValue(
                () => runsFinished(monitor).Count == 1 ? 1 : (int?)null,
                "the first warm command's run never closed. the runner said:\n  " + log());

            runsStarted(monitor).Count.ShouldBe(1);

            // Sent the way a monitor has to send it: one command at a time is the contract, and
            // "busy" is the runner's answer to a second one — so a client that wants two runs
            // asks again rather than assuming the first answer was yes.
            //
            // This is not belt and braces. run_finished is published inside the run, a hair
            // before the runner's in-flight slot clears, so a command sent the instant it
            // arrives can still be refused. On a laptop the gap is invisible; on a loaded CI
            // runner it is not, and this test failed there three tags in a row with the product
            // behaving exactly as specified.
            await sendUntilAccepted(
                monitor,
                "2",
                new RunCommand("w-2", ["Shipping/A shipment is labelled"], RunnerWire.WarmMode));

            await eventuallyValue(
                () => runsStarted(monitor).Count == 2 ? 2 : (int?)null,
                "the second warm command did not publish a run of its own. the runner said:\n  " + log());

            await eventuallyValue(
                () => runsFinished(monitor).Count == 2 ? 2 : (int?)null,
                "the second warm command's run never closed. the runner said:\n  " + log());

            var runs = runsStarted(monitor);

            runs.Select(r => r.GetProperty("command").GetString()).ShouldBe(["w-1", "w-2"]);
            runs.Select(r => r.GetProperty("runId").GetString()).Distinct().Count()
                .ShouldBe(2, "two commands are two runs, not one long one");
            runs.ShouldAllBe(r => r.GetProperty("mode").GetString() == "resident");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }

    /// <summary>
    /// Issue #398: the other Gherkin entry point goes resident too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConsolePreview</c>'s <c>Main</c> is <c>BobcatRunner.Run</c> — the JasperFx command
    /// family, with no reference to Bobcat.Mtp anywhere. That is the shape of every suite written
    /// against the runner before the MTP host existed, Stoat's own specs among them, and in 0.29.0
    /// none of them could be driven from a console's run buttons: <c>--resident</c> reached the
    /// JasperFx parser as an unknown flag and <c>BOBCAT_RESIDENT</c> was ignored, so the suite ran
    /// every spec and exited 0.
    /// </para>
    /// <para>
    /// Pinned from this class rather than a new one, deliberately: it is the same claim about a
    /// second entry point, and splitting it is how one of them gains a rule the other never hears
    /// about — the reasoning <c>SpecIdentityEndToEndTests</c> uses for its two lanes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task the_command_family_entry_point_goes_resident_too()
    {
        using var monitor = new FakeMonitorHost();

        var info = new ProcessStartInfo(previewPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(previewPath)!
        };

        // Through the environment, not the argument, because that is the half 0.29.0 silently
        // ignored: an unknown flag at least errors, while BOBCAT_RESIDENT=1 ran the whole suite
        // and exited 0 — which a parent relaunching on 0 would run in a loop forever.
        info.Environment[ResidentMode.Variable] = "1";
        info.Environment["BOBCAT_MONITOR_URL"] = monitor.Url;
        info.Environment["BOBCAT_MONITOR"] = "1";

        using var process = Process.Start(info)!;
        try
        {
            var registration = await eventually(
                () => payload<RunnerRegistration>(monitor, RunnerWire.RegisteredType),
                "the command-family host never registered");

            registration.Suite.ShouldBe("ConsolePreview");
            registration.Lane.ShouldBe("gherkin");
            registration.Specs.ShouldContain("Calculator/Add two numbers");

            // It is a resident runner in full, not a flag that merely parses: it takes a command,
            // runs the one specification named, and exits on a restart with the code that means
            // relaunch me.
            await sendUntilAccepted(
                monitor, "1", new RunCommand("p-1", ["Calculator/Add two numbers"]));

            var started = await eventuallyValue(
                () => runStarted(monitor),
                "the commanded run never published run_started");

            started.GetProperty("command").GetString().ShouldBe("p-1");
            started.GetProperty("totalScenarios").GetInt32().ShouldBe(1);

            monitor.Send("2", CloudEvent.From(
                "stoat", RunnerWire.RestartCommandType, new RestartCommand("p-restart")));

            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            process.ExitCode.ShouldBe(ResidentMode.RestartExitCode);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }

    [Fact]
    public async Task a_resident_host_with_no_monitor_stays_up_rather_than_exiting()
    {
        // The invariant, at the altitude a person sees it: `dotnet watch -- --resident` with no
        // console running must leave a runner waiting, not a process that died on startup and
        // will not come back until someone edits a file.
        var info = new ProcessStartInfo(hostPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!
        };

        info.ArgumentList.Add(ResidentMode.Option);
        info.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";
        info.Environment["BOBCAT_MONITOR_URL"] = "http://127.0.0.1:1";

        // "Nothing is listening" is the case under test, which is not the same case as "the
        // publisher is switched off" — so say which one this is.
        info.Environment["BOBCAT_MONITOR"] = "1";

        using var process = Process.Start(info)!;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            process.HasExited.ShouldBeFalse("a runner with no monitor should be waiting, not gone");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }
}
