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
    /// <summary>Stderr lines kept for diagnostics. Bounded — a chatty worker must not grow this forever.</summary>
    private const int standardErrorLinesKept = 20;

    private readonly Process _process;
    private readonly JsonRpcConnection _rpc;
    private readonly ServerModeListener _listener;
    private readonly Dictionary<string, WorkerOutcome> _outcomes = new();
    private readonly Queue<string> _standardError;
    private readonly object _lock = new();
    private readonly List<Action<WorkerTestUpdate>> _testUpdateHandlers = new();

    private MtpWorkerClient(Process process, JsonRpcConnection rpc, ServerModeListener listener, Queue<string> standardError)
    {
        _process = process;
        _rpc = rpc;
        _listener = listener;
        _standardError = standardError;
        _rpc.OnNotification(handleNotification);
    }

    /// <summary>How long to wait for a launched host to dial back.</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to wait for a dying worker to actually exit before reporting its code. The
    /// socket usually closes a moment before the process does, so reading the exit code
    /// immediately would almost always miss it.
    /// </summary>
    public static TimeSpan ExitCodeGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

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

        // Both pipes must be drained or a full buffer deadlocks the worker. Stderr is also kept:
        // it is where an unhandled exception lands, and it is the only explanation a crashed
        // worker ever gives us.
        var standardError = new Queue<string>();

        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (standardError)
            {
                standardError.Enqueue(e.Data);
                while (standardError.Count > standardErrorLinesKept) standardError.Dequeue();
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(ConnectTimeout);

            var stream = await listener.Accept(connect.Token);
            var rpc = new JsonRpcConnection(stream);
            rpc.Start();

            var client = new MtpWorkerClient(process, rpc, listener, standardError);

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
            tryKill(process);
            process.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<WorkerTest>> Discover(CancellationToken ct = default)
    {
        reset();
        await _rpc.Request("testing/discoverTests", new { runId = Guid.NewGuid() }, ct);

        return snapshot()
            .Select(o => new WorkerTest(o.Uid, o.DisplayName) { Traits = o.Traits })
            .ToList();
    }

    public async Task<WorkerRunResult> Run(IReadOnlyList<string>? uids = null, CancellationToken ct = default)
    {
        reset();

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
            var (fault, exitCode, standardError) = await describeFault(e);
            return new WorkerRunResult(Complete(uids, snapshot(), fault))
            {
                Fault = fault,
                ExitCode = exitCode,
                StandardError = standardError
            };
        }

        var outcomes = snapshot();

        if (uids is not null) GuardAgainstAnUnfilteredRun(uids, outcomes);

        return new WorkerRunResult(Complete(uids, outcomes, fault: null));
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
    /// <remarks>
    /// The worker's fault is stamped onto each synthesized outcome, so a report can say *why*
    /// a specific test has no result. "Indeterminate" on its own tells a user nothing they can
    /// act on.
    /// </remarks>
    internal static IReadOnlyList<WorkerOutcome> Complete(
        IReadOnlyList<string>? requested, IReadOnlyList<WorkerOutcome> outcomes, string? fault)
    {
        if (requested is null) return outcomes;

        var reported = outcomes.Select(o => o.Uid).ToHashSet(StringComparer.Ordinal);
        var explanation = fault ?? "the worker finished without reporting a result for this test";

        var missing = requested
            .Where(uid => !reported.Contains(uid))
            .Select(uid => new WorkerOutcome(uid, uid, WorkerTestState.Indeterminate)
            {
                ErrorMessage = explanation
            });

        return outcomes.Concat(missing).ToList();
    }

    /// <summary>
    /// Builds an account of a dead worker worth reading: the exit code and its last words,
    /// rather than a bare "the connection closed".
    /// </summary>
    private async Task<(string Fault, int? ExitCode, string? StandardError)> describeFault(Exception cause)
    {
        // The socket usually closes just before the process does, so give it a moment — reading
        // the exit code immediately would nearly always miss it.
        try
        {
            await _process.WaitForExitAsync(new CancellationTokenSource(ExitCodeGracePeriod).Token);
        }
        catch (OperationCanceledException)
        {
            // Still running: the connection broke without the process dying. Also worth saying.
        }

        int? exitCode = null;
        try
        {
            if (_process.HasExited) exitCode = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // Process was disposed underneath us; the exit code is simply unavailable.
        }

        var standardError = recentStandardError();

        var description = exitCode is null
            ? $"the worker stopped responding but is still running ({cause.Message})"
            : $"the worker exited with code {exitCode}";

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            description += $". Last standard error:{Environment.NewLine}{standardError}";
        }

        return (description, exitCode, standardError);
    }

    private string? recentStandardError()
    {
        lock (_standardError)
        {
            return _standardError.Count == 0 ? null : string.Join(Environment.NewLine, _standardError);
        }
    }

    /// <summary>
    /// The live tap. Every node change the worker streams is relayed to the handlers as it
    /// arrives — in-progress updates included, which is the part the outcome table below
    /// deliberately ignores. Handlers run on the RPC reader thread, so one that throws is
    /// swallowed here rather than allowed to take the connection down with it.
    /// </summary>
    public void OnTestUpdate(Action<WorkerTestUpdate> handler)
    {
        lock (_lock) _testUpdateHandlers.Add(handler);
    }

    private void handleNotification(string method, JsonNode? parameters)
    {
        if (method != "testing/testUpdates/tests" || parameters?["changes"] is not JsonArray changes) return;

        foreach (var change in changes)
        {
            if (change?["node"] is not JsonObject node) continue;
            if (node["uid"]?.ToString() is not { } uid) continue;

            var outcome = readNode(uid, node);
            var executionState = node["execution-state"]?.ToString() ?? "";

            lock (_lock)
            {
                // Nodes arrive at least twice — in-progress, then final. Never let a
                // non-terminal update overwrite a decided one.
                if (_outcomes.TryGetValue(uid, out var existing) &&
                    isTerminal(existing.State) && !isTerminal(outcome.State))
                {
                    continue;
                }

                _outcomes[uid] = outcome;
            }

            relayTestUpdate(new WorkerTestUpdate(uid, outcome.DisplayName, executionState)
            {
                State = executionState == "in-progress" ? null : outcome.State,
                Traits = outcome.Traits
            });
        }
    }

    private void relayTestUpdate(WorkerTestUpdate update)
    {
        Action<WorkerTestUpdate>[] handlers;
        lock (_lock) handlers = _testUpdateHandlers.ToArray();

        foreach (var handler in handlers)
        {
            try
            {
                handler(update);
            }
            catch
            {
                // A watcher must never be able to break the wire it is watching.
            }
        }
    }

    /// <summary>
    /// A test node is a flat object of dotted keys — <c>execution-state</c>, <c>error.message</c>,
    /// <c>time.duration-ms</c>, <c>traits</c> — not the property bag the in-process model uses.
    /// </summary>
    private static WorkerOutcome readNode(string uid, JsonObject node)
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

        return new WorkerOutcome(uid, node["display-name"]?.ToString() ?? uid, stateFrom(node["execution-state"]?.ToString()))
        {
            ErrorMessage = message,
            ErrorType = exceptionTypeFrom(message),
            StackTrace = node["error.stacktrace"]?.ToString(),
            Duration = node["time.duration-ms"] is { } ms && double.TryParse(ms.ToString(), out var value)
                ? TimeSpan.FromMilliseconds(value)
                : null,
            Traits = traits
        };
    }

    private static WorkerTestState stateFrom(string? state) => state switch
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
    private static string? exceptionTypeFrom(string? message)
    {
        if (message is null) return null;

        var separator = message.IndexOf(" : ", StringComparison.Ordinal);
        if (separator <= 0) return null;

        var candidate = message[..separator].Trim();
        return candidate.Contains('.') && !candidate.Contains(' ') ? candidate : null;
    }

    private static bool isTerminal(WorkerTestState state)
        => state is not (WorkerTestState.Indeterminate);

    private void reset()
    {
        lock (_lock) _outcomes.Clear();
    }

    private IReadOnlyList<WorkerOutcome> snapshot()
    {
        lock (_lock) return _outcomes.Values.ToList();
    }

    private static void tryKill(Process process)
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
        tryKill(_process);
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

    /// <summary>
    /// Environment for one specific worker, layered over the constructor's shared environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes parallelism safe for a suite whose classes share a database. Class-level
    /// partitioning keeps a class in one process, but it cannot stop two <em>different</em> classes
    /// that use the same schema landing in different workers — so the isolation has to be the
    /// database, and the only way to say which database is per-process environment.
    /// </para>
    /// <example>
    /// <code>
    /// new MtpWorkerFactory(path)
    /// {
    ///     EnvironmentFor = worker => new Dictionary&lt;string, string&gt;
    ///     {
    ///         ["POLECAT_TESTING_DATABASE"] = $"...;Initial Catalog=polecat_w{worker.Lane}"
    ///     }
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    public Func<WorkerLaunchContext, IReadOnlyDictionary<string, string>>? EnvironmentFor { get; init; }

    public string Description => Path.GetFileName(_executable);

    public async Task<IWorkerClient> Launch(WorkerLaunchContext context, CancellationToken ct = default)
        => await MtpWorkerClient.Launch(_executable, environmentFor(context), ct);

    // Internal for the layering test — three layers, most specific wins:
    // the context's run-scoped baseline, then the factory's shared environment, then the lane's.
    internal IReadOnlyDictionary<string, string>? environmentFor(WorkerLaunchContext context)
    {
        var perWorker = EnvironmentFor?.Invoke(context);
        if (context.Environment is null && perWorker is null) return _environment;
        if (context.Environment is null && _environment is null) return perWorker;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in new[] { context.Environment, _environment, perWorker })
        {
            if (layer is null) continue;
            foreach (var pair in layer) merged[pair.Key] = pair.Value;
        }

        return merged;
    }
}
