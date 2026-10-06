using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #394's measurements, and issue #402's fix. Can a <b>projected</b> suite run monitor
/// commands warm, the way a Gherkin suite can (issue #393)? The findings are written up in
/// <c>docs/warm-projected-runs.md</c>; these are the measurements that back them.
/// </summary>
/// <remarks>
/// <para>
/// <b>#394's verdict was "usable with caveats", and the caveat was Bobcat's, not the
/// platform's.</b> Server mode takes repeated run requests in one live process on the version src
/// pins, and the identity-to-uid join is clean. What did not work was the <i>run bracket</i>: a
/// projected suite published one <c>run_started</c> for the life of its process, so two commands
/// folded into one never-ending run.
/// </para>
/// <para>
/// <b>Issue #402 closed that, and two of these tests used to be tripwires pinning the broken
/// behaviour on purpose</b> — the same device <c>samples/BankAccountES</c> used for the Wolverine
/// overlay bug. They did their job: <c>Bobcat.Xunit</c> now ships an
/// <c>ITestSessionLifetimeHandler</c> that opens and closes the bracket per run request, so the
/// assertions below state the FIXED behaviour. The tripwires are retired and must not come back as
/// "two commands share a run" — that would be the regression.
/// </para>
/// <para>
/// Driven against <c>Bobcat.Xunit.Samples</c> because it is the only xUnit v3 host here that
/// publishes projected scenarios to the wire, and against <c>Microsoft.Testing.Platform</c>
/// <b>1.9.1</b> — the version src pins. The <c>spikes/mtp-orchestration</c> harness measured 2.x,
/// which is exactly the wrong version for this question.
/// </para>
/// </remarks>
public class WarmProjectedRunTests
{
    private static readonly string projectedHost = locate("Bobcat.Xunit.Samples");

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

    [Fact]
    public async Task server_mode_takes_several_run_requests_in_one_live_process()
    {
        // Q1 and Q2 of the issue. The process stays up and each request answers with exactly the
        // test it was asked for — which the supervisor already relies on for a same-process retry,
        // so this is less a discovery than a check that the premise holds for a projected host too.
        await using var client = await MtpWorkerClient.Launch(
            projectedHost,
            new Dictionary<string, string> { ["BOBCAT_MONITOR"] = "0" });

        var discovered = await client.Discover();
        discovered.Count.ShouldBeGreaterThan(1);

        var first = discovered.First(t => t.DisplayName.EndsWith("CalculatorSpecs.using_sentences"));
        var second = discovered.First(t => t.DisplayName.EndsWith("FactSpecs.facts_in_action"));

        var one = await client.Run([first.Uid]);
        one.Fault.ShouldBeNull();
        one.Outcomes.Select(o => o.Uid).ShouldBe([first.Uid]);

        var two = await client.Run([second.Uid]);
        two.Fault.ShouldBeNull();
        two.Outcomes.Select(o => o.Uid).ShouldBe([second.Uid]);

        // And again, to be sure nothing is consumed by being asked for once.
        var three = await client.Run([first.Uid]);
        three.Fault.ShouldBeNull();
        three.Outcomes.Select(o => o.Uid).ShouldBe([first.Uid]);
    }

    [Fact]
    public async Task a_discovered_test_can_be_found_from_a_specification_identity()
    {
        // Q3. A monitor sends identities (issue #391); server mode filters by the platform's uid,
        // which is an opaque hash. The join is the display name: xUnit v3 reports
        // "Namespace.Class.method", which is exactly what the manifest's TestClass + TestMethod
        // spell. So the translation a warm projected lane would need is one lookup, not a problem.
        await using var client = await MtpWorkerClient.Launch(
            projectedHost,
            new Dictionary<string, string> { ["BOBCAT_MONITOR"] = "0" });

        var discovered = await client.Discover();

        var byQualifiedMethod = discovered.ToDictionary(t => t.DisplayName, t => t.Uid, StringComparer.Ordinal);

        byQualifiedMethod.ShouldContainKey("Bobcat.Xunit.Samples.Specs.CalculatorSpecs.using_sentences");
    }

    [Fact]
    public async Task discovery_publishes_nothing_to_the_wire()
    {
        // Worth pinning while we are here: a warm runner would discover once at registration, and
        // a discovery that opened a run would put an empty card on the board every time.
        using var sink = new IngestSink();

        await using var client = await MtpWorkerClient.Launch(projectedHost, wiredTo(sink));

        await client.Discover();
        await Task.Delay(500);

        sink.EventTypes().ShouldBeEmpty();
    }

    [Fact]
    public async Task two_run_requests_are_two_runs_on_the_wire()
    {
        // #394's finding, now #402's fix. The bracket used to be per PROCESS: MarkerStepRun
        // latched on the first scenario and posted RunFinished from a ProcessExit handler. So in a
        // live server-mode process the second command's scenarios appended to the first command's
        // run, every one of them shared a RunId, and run_finished never arrived — which on a board
        // is the shape of a wedged run (the same thing issue #195 was about).
        //
        // Bobcat.Xunit now ships an ITestSessionLifetimeHandler, and a session is a run REQUEST:
        // measured on MTP 1.9.1, three testing/runTests requests fire it three times with three
        // distinct SessionUids, and discovery fires it not at all.
        //
        // This used to assert the opposite, with two assertions labelled TRIPWIRE. Issue #393's
        // requirement is what it now checks: each command is its own run on the wire, so a viewer
        // cannot tell a warm run from a cold one except by its speed.
        using var sink = new IngestSink();

        await using var client = await MtpWorkerClient.Launch(projectedHost, wiredTo(sink));

        var discovered = await client.Discover();
        var first = discovered.First(t => t.DisplayName.EndsWith("CalculatorSpecs.using_sentences"));
        var second = discovered.First(t => t.DisplayName.EndsWith("FactSpecs.facts_in_action"));

        await client.Run([first.Uid]);
        await Task.Delay(500);

        var afterFirst = sink.RunIds();
        afterFirst.Count.ShouldBe(1, "the first command opened a run, as it always did");

        // The run CLOSES while the process lives, which is the half that makes a card readable.
        sink.EventTypes().Count(type => type == "run_finished")
            .ShouldBe(1, "the first command's run closed when its session finished");

        await client.Run([second.Uid]);
        await Task.Delay(500);

        var runIds = sink.RunIds();
        runIds.Count.ShouldBe(2, "each command is its own run");
        runIds.Distinct().Count().ShouldBe(2, "and its own RunId — a shared one collapses the cards");

        sink.EventTypes().Count(type => type == "run_finished")
            .ShouldBe(2, "both runs closed");

        // Each command's scenarios under its OWN run. This is the assertion that would catch a
        // bracket that opened per request but never re-read the run id.
        sink.ScenarioRunIds().Distinct().Count()
            .ShouldBe(2, "a scenario is published under the run its own command opened");

        sink.EventTypes().Count(type => type == "scenario_finished").ShouldBe(2);
    }

    [Fact]
    public async Task a_one_shot_process_still_publishes_exactly_one_bracket()
    {
        // The counterweight, and the half that had to keep working: `dotnet test` is one process
        // and one run, and MarkerStepRun's ProcessExit backstop is still what closes the bracket
        // for a host with no session hook at all (a TUnit suite, or an xUnit project that has not
        // picked up the props). One run_started, one run_finished, whichever closed it.
        using var sink = new IngestSink();

        var environment = wiredTo(sink);
        var info = new ProcessStartInfo(projectedHost)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(projectedHost)!
        };

        info.ArgumentList.Add("--filter-method");
        info.ArgumentList.Add("*using_sentences*");
        foreach (var (name, value) in environment) info.Environment[name] = value;
        info.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";

        using var process = Process.Start(info)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(500);

        sink.RunIds().Count.ShouldBe(1, "one process, one run");
        sink.EventTypes().Count(type => type == "run_finished")
            .ShouldBe(1, "closed exactly once — the session hook and the backstop must not both post");
    }

    /// <summary>
    /// A worker environment pointed at <paramref name="sink"/>, with publishing switched ON
    /// explicitly.
    /// </summary>
    /// <remarks>
    /// The explicit <c>BOBCAT_MONITOR=1</c> is load-bearing: CI sets <c>BOBCAT_MONITOR=0</c> for
    /// the whole job so that spec hosts in the suite do not probe 5525, the worker inherits it,
    /// <c>MonitorPublisher.Disabled</c> short-circuits before the probe, and a test about what a
    /// worker published then fails against a publisher that was switched off. A test that wants
    /// the wire has to say so rather than trust the ambient value.
    /// </remarks>
    private static Dictionary<string, string> wiredTo(IngestSink sink)
        => new() { ["BOBCAT_MONITOR_URL"] = sink.Url, ["BOBCAT_MONITOR"] = "1" };

    /// <summary>
    /// The smallest thing that can stand in for a console's ingest route. Deliberately not the
    /// shared <c>FakeMonitorHost</c>: this project tests the supervisor, and linking a helper in
    /// for four assertions about what a worker published would be a reference in the wrong
    /// direction.
    /// </summary>
    private sealed class IngestSink : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<string> _batches = new();

        public string Url { get; }

        public IngestSink()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var context = await _listener.GetContextAsync();

                        if ((context.Request.Url?.AbsolutePath ?? "") == "/api/ingest")
                        {
                            using var reader = new StreamReader(context.Request.InputStream);
                            var body = await reader.ReadToEndAsync();
                            lock (_batches) _batches.Add(body);
                            context.Response.StatusCode = 202;
                        }
                        else
                        {
                            // Including /api/ping, which is what makes the probe succeed.
                            context.Response.StatusCode = 200;
                        }

                        context.Response.Close();
                    }
                }
                catch
                {
                    // Listener disposed — test over.
                }
            });
        }

        public IReadOnlyList<string> EventTypes() => read(e =>
            e.TryGetProperty("type", out var type) ? type.GetString() : null);

        public IReadOnlyList<string> RunIds() => read(e =>
            e.TryGetProperty("type", out var type) && type.GetString() == "run_started"
                ? e.GetProperty("runId").GetString()
                : null);

        /// <summary>
        /// The run each published scenario says it belongs to. Distinct from
        /// <see cref="RunIds"/> on purpose: a bracket that opened per request but kept handing out
        /// the first run's id would satisfy that one and fail this.
        /// </summary>
        public IReadOnlyList<string> ScenarioRunIds() => read(e =>
            e.TryGetProperty("type", out var type) && type.GetString() == "scenario_finished"
            && e.TryGetProperty("runId", out var runId)
                ? runId.GetString()
                : null);

        private List<string> read(Func<JsonElement, string?> select)
        {
            var values = new List<string>();

            lock (_batches)
            {
                foreach (var batch in _batches)
                {
                    using var document = JsonDocument.Parse(batch);
                    if (!document.RootElement.TryGetProperty("events", out var events)) continue;

                    foreach (var @event in events.EnumerateArray())
                    {
                        if (select(@event) is { } value) values.Add(value);
                    }
                }
            }

            return values;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { ((IDisposable)_listener).Dispose(); } catch { }
        }
    }
}
