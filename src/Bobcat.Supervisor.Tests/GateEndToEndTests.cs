using Bobcat.Monitoring;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The gate over a real test host (bobcat#729): <c>Bobcat.Supervisor.ProjectedWorker</c> declared
/// as a project, already built, run through the whole path — units, plan, supervisor, per-unit
/// run tag, the duration record, the report.
/// </summary>
/// <remarks>
/// In the <c>supervisor-monitor-env</c> collection because the gate's default
/// <see cref="GateDeclaration.RunTagPrefix"/> reads <c>BOBCAT_RUN_TAG</c>, which other tests in
/// that collection set.
/// </remarks>
[Collection("supervisor-monitor-env")]
public class GateEndToEndTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "bobcat-gate-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public GateEndToEndTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = new();

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }
    }

    /// <summary>The projected worker's project file and the configuration it was built in, from where its host is.</summary>
    private static (string Project, string Configuration) projectedWorker()
    {
        var host = WorkerHosts.ProjectedWorker;
        File.Exists(host).ShouldBeTrue($"The projected worker was not built at {host}");

        var frameworkDir = Path.GetDirectoryName(host)!;          // .../bin/Debug/net10.0
        var configurationDir = Path.GetDirectoryName(frameworkDir)!;
        var projectDir = Path.GetDirectoryName(Path.GetDirectoryName(configurationDir)!)!;

        return (Path.Combine(projectDir, "Bobcat.Supervisor.ProjectedWorker.csproj"), Path.GetFileName(configurationDir));
    }

    private static bool passingBasics(WorkerTest test)
        => WorkerHosts.IsNamed(test.DisplayName, "Basics.passes") || WorkerHosts.IsNamed(test.DisplayName, "Basics.also_passes");

    private GateDeclaration declaration(RecordingSink sink)
    {
        var (project, configuration) = projectedWorker();

        var gate = new GateDeclaration
        {
            Configuration = configuration,
            SkipBuild = true,
            WorkerBudget = 2,
            RunTagPrefix = "local-gates/bobcat-gate",
            PublishToMonitor = true,
            MonitorSink = sink,
            DurationsPath = Path.Combine(_temp, "gate-durations.json"),
            Log = line => { lock (_log) _log.Add(line); }
        };

        gate.Tracks.Add(new Track("worker").Check("the worker is built", () => File.Exists(WorkerHosts.ProjectedWorker).ShouldBeTrue()));
        gate.Project(project).On("worker").Supervise((_, supervisor) => supervisor.TestFilter = passingBasics);

        return gate;
    }

    [Fact]
    public async Task runs_a_real_host_as_one_unit_under_its_own_tag_and_reports_it()
    {
        var sink = new RecordingSink();
        var gate = declaration(sink);

        var results = await gate.Run(TestContext.Current.CancellationToken);

        var text = results.ToText();
        results.ExitCode.ShouldBe(0, text);
        results.AbortReason.ShouldBeNull();

        var unit = results.Units.ShouldHaveSingleItem();
        unit.Unit.Name.ShouldBe("Bobcat.Supervisor.ProjectedWorker.net10.0");
        unit.Verdict.ShouldBe(GateVerdict.Passed);
        unit.Results.ShouldNotBeNull().CleanPasses.Count.ShouldBe(2);
        unit.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
        unit.Predicted.ShouldBeNull();     // a first run
        unit.RunTag.ShouldBe("local-gates/bobcat-gate/Bobcat.Supervisor.ProjectedWorker.net10.0");

        // The track's preflight ran and passed.
        results.Preflight.ShouldHaveSingleItem().Results.Succeeded().ShouldBeTrue();

        // The unit published its own run under its own tag — bobcat#768 through the gate.
        var started = sink.Events.OfType<RunStarted>().ShouldHaveSingleItem();
        started.Tag.ShouldBe("local-gates/bobcat-gate/Bobcat.Supervisor.ProjectedWorker.net10.0");
        started.Mode.ShouldBe("supervised");
        sink.Events.OfType<RunFinished>().ShouldHaveSingleItem().ExitCode.ShouldBe(0);

        // The report names the unit with its verdict and duration.
        text.ShouldContain("✓ Bobcat.Supervisor.ProjectedWorker.net10.0");
        text.ShouldContain("2 passed");
        text.ShouldContain("(first run)");
        text.ShouldContain("Exit code 0: 1 clean");

        // And the plan was printed before anything ran.
        lock (_log)
        {
            _log.ShouldContain(l => l.StartsWith("Gate: 1 unit(s), 2 worker(s) budgeted"));
            _log.ShouldContain(l => l.Contains("1. Bobcat.Supervisor.ProjectedWorker.net10.0") && l.Contains("tracks: worker") && l.Contains("first run"));
        }

        // The duration record was written, so the next run is ordered by it.
        var recorded = GateDurations.Load(Path.Combine(_temp, "gate-durations.json"));
        recorded.Predict("Bobcat.Supervisor.ProjectedWorker.net10.0").ShouldNotBeNull();

        // Run again: predicted from the record this time.
        var second = await declaration(new RecordingSink()).Run(TestContext.Current.CancellationToken);
        second.Units.Single().Predicted.ShouldNotBeNull();
        second.ToText().ShouldContain("(predicted ");
    }

    [Fact]
    public async Task a_failed_track_preflight_aborts_before_any_host_is_launched()
    {
        var sink = new RecordingSink();
        var gate = declaration(sink);
        var launched = false;
        gate.WorkerFactory = unit =>
        {
            launched = true;
            return new MtpWorkerFactory(unit.Executable);
        };
        gate.Tracks.Add(new Track("broker").Check("the broker answers", () => throw new InvalidOperationException("nothing on 5672")));
        gate.Projects[0].On("broker");

        var results = await gate.Run(TestContext.Current.CancellationToken);

        results.ExitCode.ShouldBe(2);
        results.AbortReason.ShouldNotBeNull().ShouldContain("broker");
        results.Units.ShouldHaveSingleItem().Verdict.ShouldBe(GateVerdict.NotRun);
        results.Preflight.Single(p => p.Track.Name == "broker").Results.Succeeded().ShouldBeFalse();

        launched.ShouldBeFalse();
        sink.Events.ShouldBeEmpty();

        var text = results.ToText();
        text.ShouldContain("✗ track broker");
        text.ShouldContain("nothing on 5672");
        text.ShouldContain("- Bobcat.Supervisor.ProjectedWorker.net10.0");
    }

    [Fact]
    public async Task the_default_build_builds_a_solution_whose_other_projects_do_not_target_the_units_framework()
    {
        // The Marten shape (bobcat#774): a netstandard2.0 generator beside a net10.0 host. With
        // -f net10.0 on the solution the generator failed to build and nothing ran.
        var root = Path.Combine(_temp, "mixed");
        writeProject(root, "Generator", "<TargetFramework>netstandard2.0</TargetFramework>", null);
        writeProject(root, "Host", "<TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>",
            "System.Console.WriteLine(\"not a test host\");");
        var solution = Path.Combine(root, "Mixed.slnx");
        File.WriteAllText(solution,
            "<Solution><Project Path=\"Generator/Generator.csproj\" /><Project Path=\"Host/Host.csproj\" /></Solution>");

        var gate = new GateDeclaration
        {
            Solution = solution,
            PublishToMonitor = false,
            DurationsPath = null,
            Log = line => { lock (_log) _log.Add(line); }
        };
        // The host is no test host; its factory says so, which proves the build produced it.
        gate.Project(Path.Combine(root, "Host"))
            .LaunchingWith(_ => throw new InvalidOperationException("built, not supervisable"));

        var results = await gate.Run(TestContext.Current.CancellationToken);

        results.AbortReason.ShouldBeNull(results.ToText());
        results.Units.Single().Error.ShouldNotBeNull().Message.ShouldBe("built, not supervisable");
        lock (_log)
        {
            _log.ShouldContain($"dotnet build \"{solution}\" -c Debug --nologo -v quiet");
            _log.ShouldNotContain(line => line.StartsWith("dotnet build") && line.Contains(" -f "));
        }
    }

    private static void writeProject(string root, string name, string properties, string? program)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{properties}</PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(directory, name + ".cs"),
            program ?? "namespace Generated { public static class Marker { } }");
    }

    [Fact]
    public async Task a_missing_host_aborts_with_the_path_it_looked_for()
    {
        var gate = declaration(new RecordingSink());
        gate.Configuration = "NoSuchConfiguration";

        var results = await gate.Run(TestContext.Current.CancellationToken);

        results.ExitCode.ShouldBe(2);
        results.AbortReason.ShouldNotBeNull().ShouldContain(Path.Combine("bin", "NoSuchConfiguration", "net10.0"));
        results.Units.Single().Verdict.ShouldBe(GateVerdict.NotRun);
    }

    [Fact]
    public async Task a_unit_keeps_its_projects_own_retry_policy_and_a_pass_on_retry_is_reported_flaky()
    {
        var sink = new RecordingSink();
        var gate = declaration(sink);

        // The project's own configuration, as its CI target would write it: the flaky probe armed
        // through the worker's environment, and a budget that lets its Retry trait act.
        var flakyState = Path.Combine(_temp, "flaky-state.txt");
        gate.Projects[0]
            .LaunchingWith(unit => new MtpWorkerFactory(unit.Executable,
                new Dictionary<string, string> { ["BOBCAT_FLAKY_STATE"] = flakyState }))
            .Supervise((_, supervisor) =>
            {
                supervisor.TestFilter = test => WorkerHosts.IsNamed(test.DisplayName, "Fussy.flaky_until_second_attempt");
                supervisor.RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 };
            });

        var results = await gate.Run(TestContext.Current.CancellationToken);

        var unit = results.Units.ShouldHaveSingleItem();
        unit.Verdict.ShouldBe(GateVerdict.PassedOnRetry, results.ToText());
        results.ExitCode.ShouldBe(0);
        results.Flaky.Count.ShouldBe(1);
        results.ToText().ShouldContain("~ Bobcat.Supervisor.ProjectedWorker.net10.0");
        results.ToText().ShouldContain("1 passed on retry");
    }
}
