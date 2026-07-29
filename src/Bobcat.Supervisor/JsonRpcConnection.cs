using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bobcat.Supervisor;

/// <summary>
/// A JSON-RPC 2.0 client over Microsoft.Testing.Platform's server mode. Framing is LSP-style:
/// a <c>Content-Length</c> header, a blank line, then a UTF-8 body.
/// </summary>
/// <remarks>
/// Microsoft ships no client for this surface — IDEs implement it themselves — so the supervisor
/// owns it. Kept deliberately small and free of any MTP SDK dependency: the protocol is plain
/// JSON-RPC, and not referencing the platform assembly is what keeps the supervisor able to
/// drive xUnit v3 and tUnit workers as readily as Bobcat's own.
/// </remarks>
internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly List<Action<string, JsonNode?>> _handlers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _readLoop;
    private int _nextId;

    public JsonRpcConnection(Stream stream) => _stream = stream;

    /// <summary>Set when the peer went away — how a worker crash first becomes visible.</summary>
    public Exception? Faulted { get; private set; }

    public void OnNotification(Action<string, JsonNode?> handler) => _handlers.Add(handler);

    public void Start() => _readLoop = Task.Run(ReadLoop);

    public async Task<JsonNode?> Request(string method, object? parameters, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        await Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters is null ? null : JsonSerializer.SerializeToNode(parameters)
        });

        return await completion.Task.WaitAsync(ct);
    }

    public Task Notify(string method, object? parameters = null)
        => Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters is null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters)
        });

    private async Task Send(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
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
                if (body is null) break;

                Dispatch(JsonNode.Parse(body));
            }
        }
        catch (Exception e) when (!_shutdown.IsCancellationRequested)
        {
            Faulted = e;
        }
        finally
        {
            // Nothing in flight can ever be answered now. Faulting the pending requests rather
            // than letting them hang is what turns a dead worker into a prompt, visible error.
            var reason = Faulted ?? new IOException("The worker closed the connection.");
            foreach (var pending in _pending) pending.Value.TrySetException(reason);
            _pending.Clear();
        }
    }

    private void Dispatch(JsonNode? message)
    {
        if (message is not JsonObject obj) return;

        var hasId = obj.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var isResponse = hasId && (obj.ContainsKey("result") || obj.ContainsKey("error"));

        if (isResponse && int.TryParse(idNode!.ToString(), out var requestId) &&
            _pending.TryRemove(requestId, out var completion))
        {
            if (obj.TryGetPropertyValue("error", out var error) && error is not null)
                completion.TrySetException(new WorkerProtocolException(error.ToJsonString()));
            else
                completion.TrySetResult(obj["result"]);

            return;
        }

        if (!obj.TryGetPropertyValue("method", out var method) || method is null) return;

        foreach (var handler in _handlers) handler(method.ToString(), obj["params"]);

        // The platform sends us requests too (logging, telemetry). Answer them or it blocks.
        if (hasId)
        {
            _ = Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode!.DeepClone(),
                ["result"] = new JsonObject()
            });
        }
    }

    private async Task<string?> ReadFrame()
    {
        var contentLength = 0;

        while (true)
        {
            var line = await ReadLine();
            if (line is null) return null;
            if (line.Length == 0) break;

            var separator = line.IndexOf(':');
            if (separator > 0 &&
                line.AsSpan(0, separator).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(line.AsSpan(separator + 1).Trim());
            }
        }

        if (contentLength == 0) return null;

        var buffer = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var count = await _stream.ReadAsync(buffer.AsMemory(read, contentLength - read));
            if (count == 0) return null;
            read += count;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private async Task<string?> ReadLine()
    {
        var builder = new StringBuilder();
        var one = new byte[1];

        while (true)
        {
            var count = await _stream.ReadAsync(one.AsMemory(0, 1));
            if (count == 0) return builder.Length == 0 ? null : builder.ToString();
            if (one[0] == (byte)'\n') return builder.ToString().TrimEnd('\r');
            builder.Append((char)one[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _stream.Dispose();

        if (_readLoop is not null)
        {
            try { await _readLoop; } catch { /* shutting down */ }
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>
/// Accepts the socket a worker dials back on. MTP server mode inverts the usual roles: the
/// parent listens and passes its port via <c>--client-port</c>, and the test host connects out.
/// </summary>
internal sealed class ServerModeListener : IDisposable
{
    private readonly TcpListener _listener;

    public ServerModeListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
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

/// <summary>The worker answered, but not with something the supervisor can act on.</summary>
public sealed class WorkerProtocolException(string message)
    : Exception($"The worker returned a protocol error: {message}");
