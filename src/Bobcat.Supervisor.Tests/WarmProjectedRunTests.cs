using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #394: can a <b>projected</b> suite run monitor commands warm, the way a Gherkin suite can
/// (issue #393)? Measured rather than reasoned about — the findings are written up in
/// <c>docs/warm-projected-runs.md</c>, and these are the measurements.
/// </summary>
/// <remarks>
/// <para>
/// <b>The verdict is "usable with caveats", and the caveat is Bobcat's, not the platform's.</b>
/// Server mode takes repeated run requests in one live process on the version src pins, and the
/// identity-to-uid join is clean. What does not work is the <i>run bracket</i>: a projected suite
/// publishes one <c>run_started</c> for the life of its process, so two commands fold into one
/// never-ending run.
/// </para>
/// <para>
/// <b>Two of these assertions are tripwires that pin the broken behaviour on purpose</b>, the same
/// device <c>samples/BankAccountES</c> used for the Wolverine overlay bug: when someone gives
/// <c>MarkerStepRun</c> a per-request bracket, the assertion fails and tells them the blocker is
/// gone instead of letting the finding quietly go stale. They are marked below.
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
    public async Task two_run_requests_fold_into_one_run_on_the_wire_which_is_the_blocker()
    {
        // THE FINDING, and a tripwire. A projected suite's run bracket is per PROCESS:
        // MarkerStepRun latches on the first scenario and posts RunFinished from a ProcessExit
        // handler. So in a live server-mode process the second command's scenarios append to the
        // first command's run, every one of them shares a RunId, and run_finished never arrives —
        // which on a board is the shape of a wedged run (the same thing issue #195 was about).
        //
        // Issue #393 requires the opposite for warm mode: each command is its own run, so a viewer
        // cannot tell warm from cold except by speed. That is why the projected lane stays
        // cold-only, and the gap is Bobcat's to close, not the platform's.
        //
        // WHEN THIS FAILS, THE BLOCKER IS GONE. Read docs/warm-projected-runs.md, not this comment.
        using var sink = new IngestSink();

        await using var client = await MtpWorkerClient.Launch(projectedHost, wiredTo(sink));

        var discovered = await client.Discover();
        var first = discovered.First(t => t.DisplayName.EndsWith("CalculatorSpecs.using_sentences"));
        var second = discovered.First(t => t.DisplayName.EndsWith("FactSpecs.facts_in_action"));

        await client.Run([first.Uid]);
        await Task.Delay(500);

        sink.RunIds().Count.ShouldBe(1, "the first command opened a run, as it should");

        await client.Run([second.Uid]);
        await Task.Delay(500);

        sink.RunIds().Count.ShouldBe(1, "TRIPWIRE: a second run_started would mean the blocker is fixed");
        sink.EventTypes().Count(type => type == "scenario_finished")
            .ShouldBe(2, "both commands' scenarios were published — into the one run");
        sink.EventTypes().ShouldNotContain(
            "run_finished", "TRIPWIRE: the run never closes while the process lives");
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
