using System.Net;
using System.Net.Sockets;

namespace Bobcat.Tests.Monitoring;

/// <summary>
/// A minimal stand-in for the run console host: answers /api/ping, captures /api/ingest
/// bodies, and serves the Event Model wire (issue #268's <c>GET /api/event-model</c> and
/// <c>PUT /api/event-model/{source}</c>), so both publishers are tested against real HTTP.
/// </summary>
/// <remarks>
/// A file of its own since issue #294, because two test classes now need it —
/// <see cref="MonitorPublisherTests"/> for the event pump and
/// <see cref="SpecEventModelPublishingTests"/> for the Event Model half. A second copy would be
/// a second thing to keep correct, and the port-binding and double-dispose notes below were both
/// paid for once already.
/// </remarks>
internal sealed class FakeMonitorHost : IDisposable
{
    private HttpListener _listener = new();
    private readonly List<string> _batches = new();
    private readonly List<(string Source, string Body)> _eventModelPushes = new();

    public string Url { get; }

    public FakeMonitorHost()
    {
        // freePort() finds a port by binding and releasing a TcpListener, and HttpListener
        // binds it again a moment later — on a busy CI box another process can take it in
        // between ("Address already in use", seen on PR #131). Retry with a fresh port; the
        // race is narrow, so a handful of attempts is plenty, and the last failure propagates.
        HttpListenerException? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = freePort();
            Url = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"{Url}/");
            try
            {
                _listener.Start();
                _ = Task.Run(loop);
                return;
            }
            catch (HttpListenerException e)
            {
                last = e;
                ((IDisposable)_listener).Dispose();
            }
        }

        throw new InvalidOperationException("Could not bind a loopback port for the fake monitor host after 5 attempts.", last);
    }

    public IReadOnlyList<string> Batches
    {
        get { lock (_batches) return _batches.ToArray(); }
    }

    /// <summary>
    /// The model name <c>GET /api/event-model</c> reports, or null for "nothing published yet",
    /// which the real console answers as a 404. This is the whole input to the model-name rule
    /// a producer has to respect (issue #294), so the tests set it directly rather than getting
    /// there through a push.
    /// </summary>
    public string? CurrentEventModelName { get; set; }

    /// <summary>Refuse every push with the 400 the real store returns for a body it cannot parse.</summary>
    public bool RejectEventModelPushes { get; set; }

    /// <summary>Every <c>PUT /api/event-model/{source}</c> this host took, in arrival order.</summary>
    public IReadOnlyList<(string Source, string Body)> EventModelPushes
    {
        get { lock (_eventModelPushes) return _eventModelPushes.ToArray(); }
    }

    private async Task loop()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                var path = context.Request.Url?.AbsolutePath ?? "";

                if (path == "/api/ingest")
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    lock (_batches) _batches.Add(body);
                    context.Response.StatusCode = 202;
                }
                else if (path == "/api/event-model" && context.Request.HttpMethod == "GET")
                {
                    if (CurrentEventModelName is null)
                    {
                        context.Response.StatusCode = 404;
                    }
                    else
                    {
                        // Only the name matters to a producer deciding whether to push, and the
                        // real console serves it camelCase inside the merged descriptor.
                        var json = $"{{\"name\":\"{CurrentEventModelName}\",\"slices\":[]}}";
                        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                }
                else if (path.StartsWith("/api/event-model/") && context.Request.HttpMethod == "PUT")
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    var source = Uri.UnescapeDataString(path["/api/event-model/".Length..]);

                    if (RejectEventModelPushes)
                    {
                        context.Response.StatusCode = 400;
                        var bytes = System.Text.Encoding.UTF8.GetBytes("Not an EventModelDescriptor: no.");
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    else
                    {
                        lock (_eventModelPushes) _eventModelPushes.Add((source, body));
                        context.Response.StatusCode = 204;
                    }
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

    private bool _disposed;

    /// <summary>
    /// Idempotent, because this host is disposed <b>twice by design</b>: once explicitly by
    /// <c>disposing_with_a_dead_monitor_does_not_hang</c>, which kills the monitor mid-run as
    /// the whole point of the test, and once again by its <c>using</c> at scope exit.
    /// </summary>
    /// <remarks>
    /// Without the guard the second call reached <c>HttpListener.Dispose()</c> on an already
    /// disposed listener, which walks <c>RemoveListener → RemovePrefixInternal →
    /// GetEPListener</c> and <b>re-binds the port</b> to remove a prefix that is already gone.
    /// If anything had taken that port in between, it threw:
    /// <code>
    /// System.Net.HttpListenerException : Address already in use
    ///    at System.Net.HttpEndPointManager.GetEPListener(...)
    ///    at System.Net.HttpListener.Dispose()
    ///    at FakeMonitorHost.Dispose()
    /// </code>
    /// A race, so it failed roughly one run in six and never in the same place — the shape
    /// that reads as "the suite is flaky" rather than as the ordinary double-dispose bug it
    /// is. An IDisposable that throws on a second Dispose is broken whatever the odds.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _listener.Stop(); } catch { }
        try { ((IDisposable)_listener).Dispose(); } catch { }
    }
}
