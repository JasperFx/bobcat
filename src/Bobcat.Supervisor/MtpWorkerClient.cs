using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Bobcat.Supervisor;

/// <summary>
/// Launches a Microsoft.Testing.Platform test host and drives it over server mode.
/// </summary>
/// <remarks>
/// Works against any MTP host — Bobcat's own (<c>Bobcat.Mtp</c>), xUnit v3, or tUnit — which is
/// what makes the supervisor a <c>dotnet test</c> alternative rather than a Gherkin-only tool.
/// </remarks>
public sealed class MtpWorkerClient : IWorkerClient
{
    private readonly Process _process;
    private readonly JsonRpcConnection _rpc;
    private readonly ServerModeListener _listener;
    private readonly Dictionary<string, WorkerOutcome> _outcomes = new();
    private readonly object _lock = new();

    private MtpWorkerClient(Process process, JsonRpcConnection rpc, ServerModeListener listener)
    {
        _process = process;
        _rpc = rpc;
        _listener = listener;
        _rpc.OnNotification(HandleNotification);
    }

    /// <summary>How long to wait for a launched host to dial back.</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public static async Task<MtpWorkerClient> Launch(
        string executable,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var listener = new ServerModeListener();

        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };

        info.ArgumentList.Add("--server");
        info.ArgumentList.Add("jsonrpc");
        info.ArgumentList.Add("--client-port");
        info.ArgumentList.Add(listener.Port.ToString());

        // A worker must not outlive the supervisor that owns it.
        info.ArgumentList.Add("--exit-on-process-exit");
        info.ArgumentList.Add(Environment.ProcessId.ToString());

        info.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (environment is not null)
        {
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start the worker '{executable}'.");

        // Drain the pipes; a full buffer would deadlock the worker.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(ConnectTimeout);

            var stream = await listener.Accept(connect.Token);
            var rpc = new JsonRpcConnection(stream);
            rpc.Start();

            var client = new MtpWorkerClient(process, rpc, listener);

            await rpc.Request("initialize", new
            {
                processId = Environment.ProcessId,
                clientInfo = new { name = "Bobcat.Supervisor", version = "1.0.0" },
                capabilities = new { testing = new { debuggerProvider = false } }
            }, ct);

            return client;
        }
        catch
        {
            listener.Dispose();
            TryKill(process);
            process.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<WorkerTest>> Discover(CancellationToken ct = default)
    {
        Reset();
        await _rpc.Request("testing/discoverTests", new { runId = Guid.NewGuid() }, ct);

        return Snapshot()
            .Select(o => new WorkerTest(o.Uid, o.DisplayName) { Traits = o.Traits })
            .ToList();
    }

    public async Task<WorkerRunResult> Run(IReadOnlyList<string>? uids = null, CancellationToken ct = default)
    {
        Reset();

        // The subset parameter is `tests`. Getting this name wrong is NOT an error — the platform
        // silently ignores an unknown property and runs the whole suite. Hence the guard below.
        object parameters = uids is null
            ? new { runId = Guid.NewGuid() }
            : new
            {
                runId = Guid.NewGuid(),
                tests = uids.Select(uid => new JsonObject
                {
                    ["uid"] = uid,
                    ["display-name"] = uid,
                    ["node-type"] = "action"
                }).ToArray()
            };

        try
        {
            await _rpc.Request("testing/runTests", parameters, ct);
        }
        catch (Exception e) when (e is IOException or WorkerProtocolException or ObjectDisposedException)
        {
            return new WorkerRunResult(Complete(uids, Snapshot())) { Fault = e.Message };
        }

        var outcomes = Snapshot();

        if (uids is not null) GuardAgainstAnUnfilteredRun(uids, outcomes);

        return new WorkerRunResult(Complete(uids, outcomes));
    }

    /// <summary>
    /// Fails loudly if a filtered request came back with tests we did not ask for.
    /// </summary>
    /// <remarks>
    /// The #43 spike found MTP silently ignores an unrecognised subset parameter and runs
    /// everything, which is indistinguishable from a filter that matched everything. Left
    /// unchecked, a single-test retry could quietly re-run the whole suite and fold unrelated
    /// failures into the attempt — a correctness bug, not a performance one. Cheap to assert,
    /// so assert it every time.
    /// </remarks>
    internal static void GuardAgainstAnUnfilteredRun(IReadOnlyList<string> requested, IReadOnlyList<WorkerOutcome> outcomes)
    {
        var asked = requested.ToHashSet(StringComparer.Ordinal);
        var unexpected = outcomes.Where(o => !asked.Contains(o.Uid)).Select(o => o.Uid).Take(5).ToList();

        if (unexpected.Count == 0) return;

        throw new WorkerProtocolException(
            $"asked it to run {requested.Count} test(s) but it reported {outcomes.Count}, including " +
            $"[{string.Join(", ", unexpected)}]. The run was not filtered — treating this as a " +
            "protocol fault rather than trusting the results.");
    }

    /// <summary>
    /// Anything requested but unreported becomes <see cref="WorkerTestState.Indeterminate"/> —
    /// never "failed". A crashed worker loses results it had already produced, so silence is
    /// an absence of evidence, not evidence of failure.
    /// </summary>
    internal static IReadOnlyList<WorkerOutcome> Complete(
        IReadOnlyList<string>? requested, IReadOnlyList<WorkerOutcome> outcomes)
    {
        if (requested is null) return outcomes;

        var reported = outcomes.Select(o => o.Uid).ToHashSet(StringComparer.Ordinal);
        var missing = requested
            .Where(uid => !reported.Contains(uid))
            .Select(uid => new WorkerOutcome(uid, uid, WorkerTestState.Indeterminate));

        return outcomes.Concat(missing).ToList();
    }

    private void HandleNotification(string method, JsonNode? parameters)
    {
        if (method != "testing/testUpdates/tests" || parameters?["changes"] is not JsonArray changes) return;

        foreach (var change in changes)
        {
            if (change?["node"] is not JsonObject node) continue;
            if (node["uid"]?.ToString() is not { } uid) continue;

            var outcome = ReadNode(uid, node);

            lock (_lock)
            {
                // Nodes arrive at least twice — in-progress, then final. Never let a
                // non-terminal update overwrite a decided one.
                if (_outcomes.TryGetValue(uid, out var existing) &&
                    IsTerminal(existing.State) && !IsTerminal(outcome.State))
                {
                    continue;
                }

                _outcomes[uid] = outcome;
            }
        }
    }

    /// <summary>
    /// A test node is a flat object of dotted keys — <c>execution-state</c>, <c>error.message</c>,
    /// <c>time.duration-ms</c>, <c>traits</c> — not the property bag the in-process model uses.
    /// </summary>
    private static WorkerOutcome ReadNode(string uid, JsonObject node)
    {
        var traits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node["traits"] is JsonArray traitArray)
        {
            // Each entry is a single-key object: [{"Isolated":"true"},{"RecycleOnRetry":"rabbit"}]
            foreach (var entry in traitArray)
            {
                if (entry is not JsonObject pair) continue;
                foreach (var kvp in pair) traits[kvp.Key] = kvp.Value?.ToString() ?? "";
            }
        }

        var message = node["error.message"]?.ToString();

        return new WorkerOutcome(uid, node["display-name"]?.ToString() ?? uid, StateFrom(node["execution-state"]?.ToString()))
        {
            ErrorMessage = message,
            ErrorType = ExceptionTypeFrom(message),
            StackTrace = node["error.stacktrace"]?.ToString(),
            Duration = node["time.duration-ms"] is { } ms && double.TryParse(ms.ToString(), out var value)
                ? TimeSpan.FromMilliseconds(value)
                : null,
            Traits = traits
        };
    }

    private static WorkerTestState StateFrom(string? state) => state switch
    {
        "passed" => WorkerTestState.Passed,
        "failed" => WorkerTestState.Failed,
        "error" => WorkerTestState.Error,
        "skipped" => WorkerTestState.Skipped,
        "timeout" => WorkerTestState.Timeout,
        "cancelled" => WorkerTestState.Cancelled,
        _ => WorkerTestState.Indeterminate
    };

    /// <summary>
    /// There is no <c>error.type</c> on the wire. xUnit formats the message as
    /// "Namespace.ExceptionType : message" so the type is recoverable by convention; tUnit drops
    /// it entirely. Best-effort only — this is exactly why policy keys off traits instead.
    /// </summary>
    private static string? ExceptionTypeFrom(string? message)
    {
        if (message is null) return null;

        var separator = message.IndexOf(" : ", StringComparison.Ordinal);
        if (separator <= 0) return null;

        var candidate = message[..separator].Trim();
        return candidate.Contains('.') && !candidate.Contains(' ') ? candidate : null;
    }

    private static bool IsTerminal(WorkerTestState state)
        => state is not (WorkerTestState.Indeterminate);

    private void Reset()
    {
        lock (_lock) _outcomes.Clear();
    }

    private IReadOnlyList<WorkerOutcome> Snapshot()
    {
        lock (_lock) return _outcomes.Values.ToList();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await _rpc.Notify("exit");
                await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }
        catch { /* the worker may already be gone — a valid observation, not an error */ }

        await _rpc.DisposeAsync();
        _listener.Dispose();
        TryKill(_process);
        _process.Dispose();
    }
}

/// <summary>Launches <see cref="MtpWorkerClient"/>s against one test host executable.</summary>
public sealed class MtpWorkerFactory : IWorkerFactory
{
    private readonly string _executable;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    public MtpWorkerFactory(string executable, IReadOnlyDictionary<string, string>? environment = null)
    {
        _executable = executable;
        _environment = environment;
    }

    public string Description => Path.GetFileName(_executable);

    public Task<IWorkerClient> Launch(CancellationToken ct = default)
        => MtpWorkerClient.Launch(_executable, _environment, ct).ContinueWith(
            t => (IWorkerClient)t.GetAwaiter().GetResult(), ct,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
