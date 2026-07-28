using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Spike.Orchestrator;

/// <summary>One test outcome as the parent observed it over the wire.</summary>
public record TestOutcome(
    string Uid,
    string DisplayName,
    string State,
    string? ErrorType,
    string? ErrorMessage,
    string? StackTrace,
    TimeSpan? Duration,
    IReadOnlyDictionary<string, string> Traits)
{
    /// <summary>Set only when the host reported a structured assertion mismatch.</summary>
    public string? AssertActual { get; init; }

    public string? AssertExpected { get; init; }

    public string? SourceFile { get; init; }

    public int? SourceLine { get; init; }

    /// <summary>
    /// True when the host distinguished "an assertion disagreed" from "an exception escaped".
    /// This is the native signal a <c>Disposition</c> policy would key off first.
    /// </summary>
    public bool IsAssertionFailure => State == "failed";

    public bool IsError => State == "error";

    public override string ToString()
    {
        var timing = Duration is null ? "" : $" ({Duration.Value.TotalMilliseconds:F0}ms)";
        var error = ErrorMessage is null ? "" : $" — {ErrorType}: {FirstLine(ErrorMessage)}";
        return $"{State,-12} {DisplayName}{timing}{error}";
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }
}

/// <summary>The result of one whole parent-driven session against one test host process.</summary>
public record SessionResult(
    IReadOnlyList<TestOutcome> Outcomes,
    TimeSpan StartupCost,
    TimeSpan TotalDuration,
    int? ExitCode,
    string? Fault,
    IReadOnlyList<string> ProtocolLog)
{
    public TestOutcome? Find(string namePart)
        => Outcomes.FirstOrDefault(o => o.DisplayName.Contains(namePart, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Drives one MTP test-host executable over server mode: launch, handshake, discover, run.
/// </summary>
public sealed class TestHostSession : IAsyncDisposable
{
    private readonly string _executable;
    private readonly Dictionary<string, string> _environment;
    private readonly List<TestOutcome> _outcomes = new();
    private readonly Dictionary<string, TestOutcome> _byUid = new();
    private readonly List<string> _hostStdout = new();

    private Process? _process;
    private JsonRpcConnection? _rpc;
    private ServerModeListener? _listener;

    public TestHostSession(string executable, Dictionary<string, string>? environment = null)
    {
        _executable = executable;
        _environment = environment ?? new Dictionary<string, string>();
    }

    public bool Verbose { get; init; }

    public TimeSpan StartupCost { get; private set; }

    public IReadOnlyList<string> HostStdout => _hostStdout;

    /// <summary>Launch the host in server mode and complete the JSON-RPC handshake.</summary>
    public async Task<JsonNode?> Connect(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        _listener = new ServerModeListener();

        var info = new ProcessStartInfo(_executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(_executable)!
        };

        info.ArgumentList.Add("--server");
        info.ArgumentList.Add("jsonrpc");
        info.ArgumentList.Add("--client-port");
        info.ArgumentList.Add(_listener.Port.ToString());

        // Keeps a stranded host from outliving the supervisor.
        info.ArgumentList.Add("--exit-on-process-exit");
        info.ArgumentList.Add(Environment.ProcessId.ToString());

        info.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        foreach (var kvp in _environment) info.Environment[kvp.Key] = kvp.Value;

        _process = Process.Start(info)!;
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_hostStdout) _hostStdout.Add(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_hostStdout) _hostStdout.Add("stderr: " + e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var accept = CancellationTokenSource.CreateLinkedTokenSource(ct);
        accept.CancelAfter(TimeSpan.FromSeconds(30));
        var stream = await _listener.Accept(accept.Token);

        _rpc = new JsonRpcConnection(stream) { LogVerbose = Verbose };
        _rpc.OnNotification(HandleNotification);
        _rpc.Start();

        var result = await _rpc.Request("initialize", new
        {
            processId = Environment.ProcessId,
            clientInfo = new { name = "Bobcat.Spike.Orchestrator", version = "0.1.0" },
            capabilities = new { testing = new { debuggerProvider = false } }
        }, ct);

        StartupCost = stopwatch.Elapsed;
        return result;
    }

    public async Task<IReadOnlyList<TestOutcome>> Discover(CancellationToken ct = default)
    {
        ResetOutcomes();
        await _rpc!.Request("testing/discoverTests", new { runId = Guid.NewGuid() }, ct);
        return Snapshot();
    }

    /// <summary>
    /// Run everything, or — when <paramref name="uids"/> is supplied — only those test nodes.
    /// The selective form is the retry / isolation lever from Q3.
    /// </summary>
    public async Task<IReadOnlyList<TestOutcome>> Run(IEnumerable<string>? uids = null, CancellationToken ct = default)
    {
        ResetOutcomes();

        // The subset parameter is `tests` — an array of bare test nodes. Getting this name wrong
        // is NOT an error: the platform silently ignores the unknown property and runs the whole
        // suite. See findings.md; a supervisor must assert on the returned count, never assume.
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

        await _rpc!.Request("testing/runTests", parameters, ct);
        return Snapshot();
    }

    /// <summary>Sends a hand-built runTests payload — used to find the filter's wire shape.</summary>
    public async Task<IReadOnlyList<TestOutcome>> RunRaw(JsonObject parameters, CancellationToken ct = default)
    {
        ResetOutcomes();
        parameters["runId"] = Guid.NewGuid().ToString();
        await _rpc!.Request("testing/runTests", parameters, ct);
        return Snapshot();
    }

    /// <summary>
    /// Starts a full run, then cancels it in-band with <c>$/cancelRequest</c> after
    /// <paramref name="after"/>. Returns whatever the host managed to report.
    /// </summary>
    public async Task<(IReadOnlyList<TestOutcome> Outcomes, string Ending, TimeSpan Elapsed)> RunThenCancel(TimeSpan after)
    {
        ResetOutcomes();
        var stopwatch = Stopwatch.StartNew();

        var (id, response) = await _rpc!.BeginRequest("testing/runTests", new { runId = Guid.NewGuid() });

        await Task.Delay(after);
        await _rpc.CancelRequest(id);

        string ending;
        try
        {
            await response.WaitAsync(TimeSpan.FromSeconds(15));
            ending = "run request returned";
        }
        catch (TimeoutException)
        {
            ending = "run request never returned (cancel ignored)";
        }
        catch (Exception e)
        {
            ending = "run request answered with: " + Condense(e.Message);
        }

        return (Snapshot(), ending, stopwatch.Elapsed);
    }

    /// <summary>Runs and reports how the session ended rather than throwing — for the crash case.</summary>
    public async Task<(IReadOnlyList<TestOutcome> Outcomes, string Ending)> RunExpectingTrouble(
        IEnumerable<string>? uids = null)
    {
        try
        {
            var outcomes = await Run(uids);
            return (outcomes, "run request returned normally");
        }
        catch (Exception e)
        {
            return (Snapshot(), $"run request threw {e.GetType().Name}: {e.Message}");
        }
    }

    private static string Condense(string message)
    {
        var flat = message.Replace("\n", " ").Replace("\r", "");
        return flat.Length <= 180 ? flat : flat[..180] + "…";
    }

    /// <summary>Waits for the host process to exit and reports its code.</summary>
    public async Task<int?> WaitForExit(TimeSpan timeout)
    {
        if (_process is null) return null;
        try
        {
            await _process.WaitForExitAsync(new CancellationTokenSource(timeout).Token);
            return _process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void HandleNotification(string method, JsonNode? parameters)
    {
        if (method != "testing/testUpdates/tests" || parameters is null) return;
        if (parameters["changes"] is not JsonArray changes) return;

        foreach (var change in changes)
        {
            if (change?["node"] is not JsonObject node) continue;

            var uid = node["uid"]?.ToString();
            if (uid is null) continue;

            var outcome = ReadNode(uid, node);

            lock (_outcomes)
            {
                // Nodes arrive twice — in-progress then final. Keep the most decided one.
                if (_byUid.TryGetValue(uid, out var existing) && IsFinal(existing.State) && !IsFinal(outcome.State))
                    continue;

                _byUid[uid] = outcome;
            }
        }
    }

    /// <summary>
    /// A test node is a flat JSON object of dotted keys — <c>execution-state</c>,
    /// <c>error.message</c>, <c>error.stacktrace</c>, <c>time.duration-ms</c>,
    /// <c>location.*</c>, <c>traits</c> — not the <c>$type</c>-tagged property bag the
    /// in-process object model uses.
    /// </summary>
    private static TestOutcome ReadNode(string uid, JsonObject node)
    {
        var state = node["execution-state"]?.ToString() ?? "unknown";
        var message = node["error.message"]?.ToString();
        var stack = node["error.stacktrace"]?.ToString();

        TimeSpan? duration = node["time.duration-ms"] is { } ms && double.TryParse(ms.ToString(), out var value)
            ? TimeSpan.FromMilliseconds(value)
            : null;

        var traits = new Dictionary<string, string>();
        if (node["traits"] is JsonArray traitArray)
        {
            // Each entry is a single-key object: [{"Isolated":"true"},{"RecycleOnRetry":"rabbit"}]
            foreach (var entry in traitArray)
            {
                if (entry is not JsonObject pair) continue;
                foreach (var kvp in pair) traits[kvp.Key] = kvp.Value?.ToString() ?? "";
            }
        }

        return new TestOutcome(
            uid,
            node["display-name"]?.ToString() ?? uid,
            state,
            ExceptionTypeFrom(message),
            message,
            stack,
            duration,
            traits)
        {
            // Present only for assertion failures — the wire's own way of separating
            // "the assertion disagreed" from "something blew up".
            AssertActual = node["assert.actual"]?.ToString(),
            AssertExpected = node["assert.expected"]?.ToString(),
            SourceFile = node["location.file"]?.ToString(),
            SourceLine = node["location.line-start"] is { } line && int.TryParse(line.ToString(), out var n) ? n : null
        };
    }

    /// <summary>
    /// There is no <c>error.type</c> key on the wire. xUnit formats the message as
    /// "Namespace.ExceptionType : message", so the type is recoverable by convention only —
    /// see the findings note, this is the weakest link for exception-shape policy.
    /// </summary>
    private static string? ExceptionTypeFrom(string? message)
    {
        if (message is null) return null;

        var colon = message.IndexOf(" : ", StringComparison.Ordinal);
        if (colon <= 0) return null;

        var candidate = message[..colon].Trim();
        return candidate.Contains('.') && !candidate.Contains(' ') ? candidate : null;
    }

    private static bool IsFinal(string state)
        => state is "passed" or "failed" or "error" or "skipped" or "timeout" or "cancelled";

    private void ResetOutcomes()
    {
        lock (_outcomes) { _outcomes.Clear(); _byUid.Clear(); }
    }

    private IReadOnlyList<TestOutcome> Snapshot()
    {
        lock (_outcomes) return _byUid.Values.OrderBy(o => o.DisplayName).ToList();
    }

    public IReadOnlyList<string> ProtocolLog => _rpc?.ProtocolLog ?? [];

    public string? Fault => _rpc?.Faulted?.Message;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_rpc is not null && _process is { HasExited: false })
            {
                await _rpc.Notify("exit");
                await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }
        catch { /* the host may already be gone — that is a valid observation, not an error */ }

        if (_rpc is not null) await _rpc.DisposeAsync();
        _listener?.Dispose();

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        _process?.Dispose();
    }

    public int? ExitCode => _process is { HasExited: true } ? _process.ExitCode : null;
}
