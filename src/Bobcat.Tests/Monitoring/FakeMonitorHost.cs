using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

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
    private readonly List<string> _runnerEvents = new();

    private readonly Channel<string> _commands =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false });

    private readonly List<string?> _lastEventIds = new();

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

    // --- The resident runner wire (issue #390).

    /// <summary>Every CloudEvent a runner POSTed to <c>/api/runners/events</c>, as raw JSON.</summary>
    public IReadOnlyList<string> RunnerEvents
    {
        get { lock (_runnerEvents) return _runnerEvents.ToArray(); }
    }

    /// <summary>
    /// The <c>Last-Event-ID</c> header of every command-stream request, in arrival order — null
    /// for a request that sent none. This is how a test sees that a reconnect resumed.
    /// </summary>
    public IReadOnlyList<string?> LastEventIds
    {
        get { lock (_lastEventIds) return _lastEventIds.ToArray(); }
    }

    /// <summary>Refuse the command stream, the way a monitor that does not know this runner would.</summary>
    public bool RefuseCommandStream { get; set; }

    /// <summary>
    /// Close the command stream after this many events have been written to it, so a test can
    /// exercise the reconnect. Zero means never.
    /// </summary>
    public int CloseCommandStreamAfter { get; set; }

    /// <summary>Queue one already-framed SSE block for the next reader of the command stream.</summary>
    public void SendRaw(string sse) => _commands.Writer.TryWrite(sse);

    /// <summary>
    /// Queue a CloudEvent as the real stream frames one: the <b>whole envelope</b> in
    /// <c>data</c>, its type as the SSE event name and its id as the SSE id.
    /// </summary>
    /// <remarks>
    /// The envelope is what goes in <c>data</c>, not the payload — the first version of this
    /// helper framed the payload, and every runner test that read a command off the stream failed
    /// with "ignored a command of type ''" because a bare payload deserializes into a CloudEvent
    /// whose every attribute is its default. Worth the sentence: a test helper that frames the
    /// wire wrongly makes correct code look broken.
    /// </remarks>
    public void Send(string id, Bobcat.Residency.CloudEvent @event)
        => SendRaw($"id: {id}\nevent: {@event.Type}\ndata: {@event.ToJson()}\n\n");

    /// <summary>A keepalive, which exists to hold the connection open and says nothing.</summary>
    public void SendKeepalive(string id)
        => SendRaw($"id: {id}\nevent: {Bobcat.Residency.RunnerWire.KeepaliveType}\ndata: {{}}\n\n");

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
                else if (path == "/api/runners/events" && context.Request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    lock (_runnerEvents) _runnerEvents.Add(body);
                    context.Response.StatusCode = 202;
                }
                else if (path.StartsWith("/api/runners/") && path.EndsWith("/commands"))
                {
                    lock (_lastEventIds) _lastEventIds.Add(context.Request.Headers["Last-Event-ID"]);

                    if (RefuseCommandStream)
                    {
                        context.Response.StatusCode = 404;
                    }
                    else
                    {
                        // Served on its own task, deliberately: a command stream is held open for
                        // as long as the runner is listening, and this loop has to keep taking the
                        // acknowledgements that runner posts while it holds it. Serving it inline
                        // deadlocks the first command.
                        _ = Task.Run(() => serveCommands(context));
                        continue;
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

    /// <summary>
    /// Writes queued commands to one reader until the stream is closed — by
    /// <see cref="CloseCommandStreamAfter"/>, or by the listener shutting down.
    /// </summary>
    private async Task serveCommands(HttpListenerContext context)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;

        var written = 0;

        try
        {
            // A comment first, the way a real stream opens: it proves the connection without
            // being an event, and it is what a reader has to learn to skip.
            await write(context, ": connected\n\n");

            while (_listener.IsListening)
            {
                using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var sse = await _commands.Reader.ReadAsync(idle.Token);

                await write(context, sse);
                written++;

                if (CloseCommandStreamAfter > 0 && written >= CloseCommandStreamAfter) break;
            }
        }
        catch
        {
            // The reader went away, or nothing arrived in time. Either way the stream is over.
        }

        try { context.Response.Close(); } catch { }
    }

    private static async Task write(HttpListenerContext context, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        await context.Response.OutputStream.WriteAsync(bytes);
        await context.Response.OutputStream.FlushAsync();
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
