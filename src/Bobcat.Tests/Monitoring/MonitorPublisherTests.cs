using System.Net;
using System.Net.Sockets;
using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

/// <summary>
/// Every test here owns the <c>BOBCAT_MONITOR</c> kill switch for its duration: the value the
/// process started with is saved, cleared so the publisher is enabled, and restored afterwards.
/// Without that these tests answer to whatever the ambient environment says — CI sets
/// <c>BOBCAT_MONITOR=0</c> so the spec hosts it collects never publish, and two of these tests
/// quietly asserted a publisher that the switch had already refused to build.
/// </summary>
public class MonitorPublisherTests : IDisposable
{
    private readonly string? _previousKillSwitch;

    public MonitorPublisherTests()
    {
        _previousKillSwitch = Environment.GetEnvironmentVariable(MonitorPublisher.KillSwitchVariable);
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, null);
    }

    public void Dispose()
        => Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, _previousKillSwitch);

    /// <summary>
    /// A minimal stand-in for the Bobcat.Console host: answers /api/ping and captures
    /// /api/ingest bodies, so the publisher is tested against real HTTP.
    /// </summary>
    private sealed class FakeMonitorHost : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<string> _batches = new();

        public string Url { get; }

        public FakeMonitorHost()
        {
            var port = freePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();
            _ = Task.Run(loop);
        }

        public IReadOnlyList<string> Batches
        {
            get { lock (_batches) return _batches.ToArray(); }
        }

        private async Task loop()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();

                    if (context.Request.Url?.AbsolutePath == "/api/ingest")
                    {
                        using var reader = new StreamReader(context.Request.InputStream);
                        var body = await reader.ReadToEndAsync();
                        lock (_batches) _batches.Add(body);
                        context.Response.StatusCode = 202;
                    }
                    else
                    {
                        context.Response.StatusCode = 200;
                    }

                    context.Response.Close();
                }
            }
            catch
            {
                // Listener disposed — test over.
            }
        }

        private static int freePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            ((IDisposable)_listener).Dispose();
        }
    }

    [Fact]
    public async Task try_connect_returns_null_quickly_when_nothing_is_listening()
    {
        // A port nothing listens on — connection refused, not a hang.
        var started = DateTime.UtcNow;
        var publisher = await MonitorPublisher.TryConnect("http://127.0.0.1:1");

        publisher.ShouldBeNull();
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task the_kill_switch_suppresses_even_the_probe()
    {
        using var host = new FakeMonitorHost();
        // Restored to the pre-test value by Dispose, never blindly to null.
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, "0");

        (await MonitorPublisher.TryConnect(host.Url)).ShouldBeNull();
    }

    [Fact]
    public async Task posted_events_arrive_in_batch_envelopes_with_snake_case_discriminators_and_camel_case_fields()
    {
        using var host = new FakeMonitorHost();
        var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        var runId = Guid.NewGuid();
        publisher.Post(new RunStarted(runId, "Suite", "/repo", "main", "in-process",
            DateTimeOffset.UtcNow, 3));
        publisher.Post(new StepStarted(runId, "F/s", "step-1", "Given", "a thing"));

        // DisposeAsync flushes what's queued before returning.
        await publisher.DisposeAsync();

        // Batching is opportunistic: the pump sends whatever is queued each time it wakes, so two
        // back-to-back posts travel as one batch on a warm process and as two on a cold one. The
        // contract is that every event arrives inside an "events" envelope — not how many
        // envelopes it takes — so that is what is asserted here.
        var batches = host.Batches;
        batches.ShouldNotBeEmpty();
        batches.ShouldAllBe(b => b.Contains("\"events\""));

        var everything = string.Join("\n", batches);
        everything.ShouldContain("\"type\":\"run_started\"");
        everything.ShouldContain("\"type\":\"step_started\"");
        everything.ShouldContain($"\"runId\":\"{runId}\"");
        everything.ShouldContain("\"totalScenarios\":3");
    }

    [Fact]
    public async Task disposing_with_a_dead_monitor_does_not_hang()
    {
        using var host = new FakeMonitorHost();
        var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        // The monitor dies mid-run; the publisher must close out promptly regardless.
        host.Dispose();
        publisher.Post(new RunHeartbeat(Guid.NewGuid(), DateTimeOffset.UtcNow));

        var closing = publisher.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.ShouldBe(closing);
    }
}
