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

    private static string locateHost()
    {
        var configuration = Path.GetFileName(Path.GetDirectoryName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, "Bobcat.Mtp.GeneratedHost", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "Bobcat.Mtp.GeneratedHost.exe" : "Bobcat.Mtp.GeneratedHost");
    }

    private static Process launch(FakeMonitorHost monitor)
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

    private static IEnumerable<CloudEvent> eventsOf(FakeMonitorHost monitor)
        => monitor.RunnerEvents.Select(json => CloudEvent.FromJson(json)!);

    private static T? payload<T>(FakeMonitorHost monitor, string type) where T : class
        => eventsOf(monitor).Where(e => e.Type == type).Select(e => e.DataAs<T>()).LastOrDefault();

    [Fact]
    public async Task a_real_host_registers_runs_a_named_specification_and_restarts_on_command()
    {
        using var monitor = new FakeMonitorHost();
        using var process = launch(monitor);

        try
        {
            // 1. Register. It arrives without anything asking for it: the runner is the client.
            var registration = await eventually(
                () => payload<RunnerRegistration>(monitor, RunnerWire.RegisteredType),
                "the host never registered");

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

            var started = await eventuallyValue(
                () => runStarted(monitor),
                "the commanded run never published run_started. the runner said:\n  " + log());

            started.GetProperty("command").GetString().ShouldBe("c-good");
            started.GetProperty("mode").GetString().ShouldBe(BobcatResidentSuite.Mode);
            started.GetProperty("totalScenarios").GetInt32()
                .ShouldBe(1, "the run was narrowed to the one specification the command named");

            // 4. Restart means exit, so a parent can relaunch. Exit code 0: a resident runner's
            //    exit says nothing about any test.
            monitor.Send("3", CloudEvent.From(
                "stoat", RunnerWire.RestartCommandType, new RestartCommand("c-restart")));

            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            process.ExitCode.ShouldBe(0);
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

            monitor.Send("2", CloudEvent.From(
                "stoat",
                RunnerWire.RunCommandType,
                new RunCommand("w-2", ["Shipping/A shipment is labelled"], RunnerWire.WarmMode)));

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
