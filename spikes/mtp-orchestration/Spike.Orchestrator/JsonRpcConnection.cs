using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Spike.Orchestrator;

/// <summary>
/// A minimal JSON-RPC 2.0 client over the MTP server-mode socket. Framing is LSP-style
/// (<c>Content-Length</c> header, blank line, UTF-8 body).
///
/// Microsoft ships no client library for this — IDEs implement it themselves — so a Bobcat
/// supervisor would own roughly this much code. That cost is part of the spike's answer.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly List<Action<string, JsonNode?>> _notificationHandlers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<string> _log = new();
    private Task? _readLoop;
    private int _nextId;

    public JsonRpcConnection(Stream stream) => _stream = stream;

    /// <summary>Every frame in both directions, for the findings note.</summary>
    public IReadOnlyList<string> ProtocolLog => _log;

    public bool LogVerbose { get; init; }

    /// <summary>Set when the peer's socket closes — how a host crash surfaces to the parent.</summary>
    public Exception? Faulted { get; private set; }

    public void OnNotification(Action<string, JsonNode?> handler) => _notificationHandlers.Add(handler);

    public void Start() => _readLoop = Task.Run(ReadLoop);

    public async Task<JsonNode?> Request(string method, object? parameters, CancellationToken ct = default)
    {
        var (_, response) = await BeginRequest(method, parameters);
        using var reg = ct.Register(() => { /* observed by the caller; see CancelRequest */ });
        return await response.WaitAsync(ct);
    }

    /// <summary>
    /// Issues a request and hands back its id, so the caller can cancel it in-band with
    /// <see cref="CancelRequest"/> rather than merely abandoning the wait.
    /// </summary>
    public async Task<(int Id, Task<JsonNode?> Response)> BeginRequest(string method, object? parameters)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters is null ? null : JsonSerializer.SerializeToNode(parameters)
        });

        return (id, tcs.Task);
    }

    /// <summary>The JSON-RPC/LSP cancellation notification.</summary>
    public Task CancelRequest(int id) => Notify("$/cancelRequest", new { id });

    public Task Notify(string method, object? parameters = null)
        => Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters is null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters)
        });

    private async Task Send(JsonObject message)
    {
        var json = message.ToJsonString();
        Record("--> " + json);

        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _writeLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(header);
            await _stream.WriteAsync(body);
            await _stream.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var body = await ReadFrame();
                if (body is null) break; // peer closed

                Record("<-- " + body);
                Dispatch(JsonNode.Parse(body));
            }
        }
        catch (Exception e)
        {
            Faulted = e;
        }
        finally
        {
            // Anything still in flight can never be answered now — this is exactly what the
            // parent observes when a test host dies mid-run.
            var reason = Faulted ?? new IOException("The test host closed the connection.");
            foreach (var kvp in _pending) kvp.Value.TrySetException(reason);
            _pending.Clear();
        }
    }

    private void Dispatch(JsonNode? message)
    {
        if (message is not JsonObject obj) return;

        // A response carries an id we issued; anything else is a server-initiated message.
        if (obj.TryGetPropertyValue("id", out var idNode) && idNode is not null &&
            obj.ContainsKey("result") || obj.ContainsKey("error"))
        {
            if (obj.TryGetPropertyValue("id", out var id) && id is not null &&
                int.TryParse(id.ToString(), out var requestId) &&
                _pending.TryRemove(requestId, out var tcs))
            {
                if (obj.TryGetPropertyValue("error", out var error) && error is not null)
                    tcs.TrySetException(new InvalidOperationException($"JSON-RPC error: {error.ToJsonString()}"));
                else
                    tcs.TrySetResult(obj["result"]);
                return;
            }
        }

        if (obj.TryGetPropertyValue("method", out var method) && method is not null)
        {
            var name = method.ToString();
            var parameters = obj["params"];

            foreach (var handler in _notificationHandlers) handler(name, parameters);

            // The platform issues real requests at us too (e.g. telemetry, logs). Answer them
            // or the host blocks waiting.
            if (obj.TryGetPropertyValue("id", out var reqId) && reqId is not null)
            {
                _ = Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = reqId.DeepClone(),
                    ["result"] = new JsonObject()
                });
            }
        }
    }

    private async Task<string?> ReadFrame()
    {
        var contentLength = 0;

        while (true)
        {
            var line = await ReadLine();
            if (line is null) return null;
            if (line.Length == 0) break; // end of headers

            var split = line.IndexOf(':');
            if (split > 0 &&
                line.AsSpan(0, split).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(line.AsSpan(split + 1).Trim());
            }
        }

        if (contentLength == 0) return null;

        var buffer = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read, contentLength - read));
            if (n == 0) return null;
            read += n;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private async Task<string?> ReadLine()
    {
        var builder = new StringBuilder();
        var one = new byte[1];

        while (true)
        {
            var n = await _stream.ReadAsync(one.AsMemory(0, 1));
            if (n == 0) return builder.Length == 0 ? null : builder.ToString();

            if (one[0] == (byte)'\n') return builder.ToString().TrimEnd('\r');
            builder.Append((char)one[0]);
        }
    }

    private void Record(string line)
    {
        lock (_log) _log.Add(line);
        if (LogVerbose) Console.WriteLine("    " + Truncate(line));
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + " …";

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _stream.Dispose();
        if (_readLoop is not null)
        {
            try { await _readLoop; } catch { /* shutting down */ }
        }
        _shutdown.Dispose();
    }
}

/// <summary>Accepts the socket the test host dials back on.</summary>
public sealed class ServerModeListener : IDisposable
{
    private readonly TcpListener _listener;

    public ServerModeListener()
    {
        _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public async Task<Stream> Accept(CancellationToken ct)
    {
        var client = await _listener.AcceptTcpClientAsync(ct);
        client.NoDelay = true;
        return client.GetStream();
    }

    public void Dispose() => _listener.Stop();
}
