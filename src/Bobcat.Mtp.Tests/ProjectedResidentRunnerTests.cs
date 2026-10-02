using System.Diagnostics;
using System.Text.Json;
using Bobcat.Residency;
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

    private async Task<OutOfProcessResidentSuite> suiteFor(FakeMonitorHost monitor)
        => await OutOfProcessResidentSuite.For(
            projectedHost, monitorUrl: monitor.Url, listingDirectory: _directory);

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
    public async Task a_projected_suite_registers_its_identities_and_offers_only_cold()
    {
        using var monitor = new FakeMonitorHost();
        var suite = await suiteFor(monitor);

        suite.Suite.ShouldBe("Bobcat.Xunit.Samples");
        suite.Lane.ShouldBe(SpecManifest.ProjectedLane);
        suite.SpecIdentities.ShouldContain(knownSpec);

        // Warm is withheld for a measured reason, not an unimplemented one: issue #394 found the
        // platform takes repeated run requests fine, and the blocker is Bobcat's own run bracket,
        // which a projected suite closes at process exit.
        suite.Modes.ShouldBe([RunnerWire.ColdMode]);
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
            registration.Modes.ShouldBe([RunnerWire.ColdMode]);
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
