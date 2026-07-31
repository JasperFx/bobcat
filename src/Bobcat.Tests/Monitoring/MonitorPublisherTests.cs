using System.Net;
using System.Net.Sockets;
using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

public class MonitorPublisherTests
{
    /// <summary>
    /// A minimal stand-in for the Bobcat.Monitor host: answers /api/ping and captures
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
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, "0");
        try
        {
            (await MonitorPublisher.TryConnect(host.Url)).ShouldBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, null);
        }
    }

    [Fact]
    public async Task posted_events_arrive_as_a_batch_with_snake_case_discriminators_and_camel_case_fields()
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

        var body = host.Batches.ShouldHaveSingleItem();
        body.ShouldContain("\"events\"");
        body.ShouldContain("\"type\":\"run_started\"");
        body.ShouldContain("\"type\":\"step_started\"");
        body.ShouldContain($"\"runId\":\"{runId}\"");
        body.ShouldContain("\"totalScenarios\":3");
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
