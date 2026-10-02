using System.Net.Http.Headers;
using System.Text;
using Bobcat.Monitoring;
using Bobcat.Runtime;

namespace Bobcat.Residency;

/// <summary>How a <see cref="ResidentRunner"/> is pointed and paced.</summary>
public sealed record ResidentRunnerOptions
{
    /// <summary>
    /// The monitor's origin. Defaults to the same one every publisher already probes, so a
    /// resident runner needs no configuration a publishing run did not already need.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Stable for the life of the process. A fresh one per start is correct: a restarted runner is
    /// a different process over possibly different code, and registering as the old one would let
    /// a monitor hold a stream open to something that no longer exists.
    /// </summary>
    public string RunnerId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>The checkout this runner speaks for. Discovered from the working directory when null.</summary>
    public string? Repository { get; init; }

    /// <summary>The branch that checkout is on. Discovered when null.</summary>
    public string? Branch { get; init; }

    /// <summary>First reconnect delay; doubled on each consecutive failure up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The ceiling on the reconnect delay. A monitor that is simply not running must cost a
    /// near-idle process, not a poll loop — and it may well come up later, so the runner keeps
    /// asking rather than giving up and leaving a person with a runner that will never answer.
    /// </summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the monitor to answer the registration POST.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Where the runner narrates itself. Nothing by default.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// A suite kept available to a monitor, running specifications <b>when the monitor asks</b>
/// (issue #390) — the Bobcat half of interactive execution.
/// </summary>
/// <remarks>
/// <para>
/// <b>The runner is a client.</b> It connects out to the monitor and asks for work. A monitor can
/// never make a runner do anything; it can only answer a runner that asked. Nothing here listens
/// on a port, so there is nothing to secure — and a command can only name specifications the
/// runner already told the monitor it has.
/// </para>
/// <para>
/// <b>The invariant from the publisher applies unchanged: a monitor that is absent, slow or
/// hostile never matters.</b> No monitor means no registration, no stream and an idle process that
/// keeps asking on a capped backoff. A dropped stream reconnects, resuming from
/// <c>Last-Event-ID</c>. Anything unparseable is ignored rather than fatal. Nothing a monitor
/// sends can end the process except the one command that means exactly that.
/// </para>
/// <para>
/// <b>One command at a time.</b> A second arriving while one is in flight is <i>rejected as
/// busy</i>, not queued: the monitor owns the queue, and a runner that silently queued would
/// leave a person waiting on a run whose turn it could not see.
/// </para>
/// <para>
/// <b>Cold by default.</b> Each command is a fresh filtered run, so it always runs the current
/// code. Warm — reusing a booted host — is a mode a suite has to offer and a command has to ask
/// for (issue #393).
/// </para>
/// </remarks>
public sealed class ResidentRunner : IAsyncDisposable
{
    private readonly IResidentSuite _suite;
    private readonly ResidentRunnerOptions _options;
    private readonly HttpClient _client;
    private readonly string _source;

    private string? _lastEventId;
    private Task? _inFlight;
    private CancellationTokenSource? _inFlightCancellation;
    private readonly object _gate = new();

    public ResidentRunner(IResidentSuite suite, ResidentRunnerOptions? options = null)
        : this(suite, options, client: null)
    {
    }

    /// <summary>For a test that wants to supply its own client. The runner owns it either way.</summary>
    internal ResidentRunner(IResidentSuite suite, ResidentRunnerOptions? options, HttpClient? client)
    {
        _suite = suite;
        _options = options ?? new ResidentRunnerOptions();
        _source = CloudEvent.SourceFor(_options.RunnerId);

        _client = client ?? new HttpClient
        {
            BaseAddress = new Uri(_options.Url ?? MonitorPublisher.ResolveUrl())
        };
    }

    public string RunnerId => _options.RunnerId;

    /// <summary>Set when the monitor asked the runner to exit so a parent can relaunch it.</summary>
    public bool RestartRequested { get; private set; }

    /// <summary>True while a command is being run.</summary>
    public bool Busy
    {
        get { lock (_gate) return _inFlight is { IsCompleted: false }; }
    }

    /// <summary>
    /// The modes on offer right now — <see cref="IResidentSuite.Modes"/>, minus
    /// <see cref="RunnerWire.WarmMode"/> once the suite has said it is no longer usable.
    /// </summary>
    public IReadOnlyList<string> AvailableModes
        => _suite.UnusableReason is null
            ? _suite.Modes
            : _suite.Modes.Where(mode => mode != RunnerWire.WarmMode).ToList();

    /// <summary>What this runner registers as.</summary>
    public RunnerRegistration Registration()
    {
        var (repository, branch) = GitInfo.Discover(Directory.GetCurrentDirectory());

        return new RunnerRegistration(
            _options.RunnerId,
            _options.Repository ?? repository ?? Directory.GetCurrentDirectory(),
            _options.Branch ?? branch,
            _suite.Suite,
            _suite.Lane,
            AvailableModes,
            _suite.SpecIdentities);
    }

    /// <summary>
    /// Register, then take commands until the token is cancelled or a <c>restart</c> arrives,
    /// reconnecting on a capped backoff whenever the monitor is absent or the stream drops.
    /// </summary>
    /// <remarks>
    /// Returns rather than throws. A resident runner that died of a network blip would be worse
    /// than one that is quietly waiting — its parent only relaunches it on a source change.
    /// </remarks>
    public async Task Run(CancellationToken token = default)
    {
        var backoff = _options.Backoff;

        while (!token.IsCancellationRequested && !RestartRequested)
        {
            var connected = false;

            try
            {
                if (await Register(token))
                {
                    connected = true;
                    backoff = _options.Backoff;
                    await Listen(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log($"the command stream failed: {e.Message}");
            }

            if (token.IsCancellationRequested || RestartRequested) break;

            // A stream that connected and then ended may well reconnect immediately; one that
            // never connected is a monitor that is not there, and the delay climbs.
            var delay = connected ? _options.Backoff : backoff;
            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!connected)
            {
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
            }
        }

        await drainInFlight();
    }

    /// <summary>
    /// Announce this runner. False when the monitor did not answer — which is not an error, only
    /// the absence of a monitor.
    /// </summary>
    /// <remarks>
    /// Re-sent on every reconnect and every restart, by design: it is idempotent, and it means a
    /// monitor that lost its own state recovers without anyone telling the runner to do anything.
    /// </remarks>
    public async Task<bool> Register(CancellationToken token = default)
    {
        var registration = Registration();
        var posted = await post(RunnerWire.RegisteredType, registration, token);

        if (posted)
        {
            log($"registered as {_options.RunnerId} — {registration.Suite} ({registration.Lane}), "
                + $"{registration.Specs.Count} specification(s), modes {string.Join("/", registration.Modes)}");
        }

        return posted;
    }

    /// <summary>
    /// Read the command stream until it ends. Each command is acknowledged before anything else
    /// happens to it, accepted or not.
    /// </summary>
    public async Task Listen(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RunnerWire.CommandsRoute(_options.RunnerId));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ServerSentEvents.MediaType));

        // Resume rather than lose whatever arrived while the stream was down.
        if (_lastEventId is { Length: > 0 } resume) request.Headers.TryAddWithoutValidation("Last-Event-ID", resume);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            log($"the monitor refused the command stream: {(int)response.StatusCode}");
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var sent in ServerSentEvents.Read(reader, token))
        {
            if (sent.Id is { Length: > 0 } id) _lastEventId = id;

            // A keepalive exists to hold the connection open and says nothing.
            if (sent.Type == RunnerWire.KeepaliveType) continue;
            if (sent.Data.Length == 0) continue;

            var @event = CloudEvent.FromJson(sent.Data);
            if (@event is null)
            {
                log($"ignored an event that is not a CloudEvent (type '{sent.Type}')");
                continue;
            }

            await Handle(@event, token);

            if (RestartRequested) return;
        }
    }

    /// <summary>
    /// Deal with one command: acknowledge it, then run it if it was accepted.
    /// </summary>
    /// <remarks>
    /// Public so the whole decision — which commands are refused and why — is testable without a
    /// stream in front of it.
    /// </remarks>
    public async Task Handle(CloudEvent @event, CancellationToken token = default)
    {
        switch (@event.Type)
        {
            case RunnerWire.RunCommandType:
                await handleRun(@event, token);
                return;

            case RunnerWire.RestartCommandType:
                await handleRestart(@event, token);
                return;

            default:
                // Not an error. A monitor is free to put anything on this stream, and a runner that
                // broke on a type it did not recognise could not be upgraded independently of it.
                log($"ignored a command of type '{@event.Type}'");
                return;
        }
    }

    private async Task handleRun(CloudEvent @event, CancellationToken token)
    {
        var command = @event.DataAs<RunCommand>();
        if (command is null || string.IsNullOrWhiteSpace(command.CommandId))
        {
            log($"ignored a {RunnerWire.RunCommandType} with no readable command id");
            return;
        }

        var refusal = refuse(command);
        if (refusal is not null)
        {
            await acknowledge(command.CommandId, accepted: false, refusal, token);
            log($"refused command {command.CommandId}: {refusal}");
            return;
        }

        await acknowledge(command.CommandId, accepted: true, reason: null, token);

        var selection = SpecSelection.Of(command.Specs ?? []);
        var mode = command.ResolvedMode;

        // Run off the stream rather than on it, so the stream keeps being read while a run is in
        // flight — which is what lets a restart arrive mid-run and be acted on.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var work = Task.Run(async () =>
        {
            try
            {
                log($"running command {command.CommandId} ({mode}): {selection}");
                await _suite.Run(command.CommandId, selection, mode, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                log($"command {command.CommandId} was cut short");
            }
            catch (Exception e)
            {
                // A red run is an outcome and travels on the ingest stream. Getting here means the
                // suite could not be run at all — which is the runner's news to report, and must
                // not be the runner's death.
                log($"command {command.CommandId} could not be run: {e.Message}");
            }
        }, CancellationToken.None);

        lock (_gate)
        {
            _inFlightCancellation?.Dispose();
            _inFlightCancellation = cancellation;
            _inFlight = work;
        }
    }

    private async Task handleRestart(CloudEvent @event, CancellationToken token)
    {
        var command = @event.DataAs<RestartCommand>();
        var commandId = command?.CommandId ?? "";

        if (commandId.Length > 0) await acknowledge(commandId, accepted: true, reason: null, token);

        RestartRequested = true;
        log("restart requested — exiting so a parent can relaunch");

        // A run in flight is cut short rather than waited on. A wedged run is the main reason
        // someone restarts a runner, so a restart that waited would be useless in exactly the case
        // it exists for. The cut-short run publishes whatever it had; the replacement starts clean.
        CancellationTokenSource? cancellation;
        lock (_gate) cancellation = _inFlightCancellation;

        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Why this command is refused, or null to accept it. The three rules issue #390 names, in the
    /// order a person would want to hear them.
    /// </summary>
    private string? refuse(RunCommand command)
    {
        if (_suite.UnusableReason is { Length: > 0 } unusable)
        {
            return $"this runner can no longer run anything: {unusable}";
        }

        if (Busy) return "this runner is already running a command";

        if (!AvailableModes.Contains(command.ResolvedMode))
        {
            // Never silently downgraded. A person who asked for warm and got cold would read the
            // resulting wall clock as warm mode not working.
            return $"'{command.ResolvedMode}' is not a mode this runner offers "
                   + $"(it offers {string.Join(", ", AvailableModes)})";
        }

        var selection = SpecSelection.Of(command.Specs ?? []);
        if (!selection.NarrowsAnything)
        {
            // Deliberately refused rather than read as "run everything": a command carrying no
            // identities is far more likely to be a mistake on the asking side than a request for
            // the whole suite, and the whole suite is what an ordinary run already does.
            return "a command has to name at least one specification";
        }

        var unknown = selection.NotIn(_suite.SpecIdentities);
        if (unknown.Count > 0)
        {
            return $"this runner has no specification named {string.Join(", ", unknown.Select(x => $"'{x}'"))}";
        }

        return null;
    }

    private Task acknowledge(string commandId, bool accepted, string? reason, CancellationToken token)
        => post(
            RunnerWire.AcknowledgedType,
            new RunnerAcknowledgement(_options.RunnerId, commandId, accepted, reason),
            token);

    private async Task<bool> post<T>(string type, T data, CancellationToken token)
    {
        try
        {
            var @event = CloudEvent.From(_source, type, data);

            using var content = new StringContent(@event.ToJson(), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue(CloudEvent.MediaType);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_options.RequestTimeout);

            using var response = await _client.PostAsync(RunnerWire.EventsRoute, content, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // An absent or slow monitor is the expected case, not an incident.
            return false;
        }
    }

    private async Task drainInFlight()
    {
        Task? work;
        lock (_gate) work = _inFlight;

        if (work is null) return;

        try { await work; }
        catch { /* handleRun already reported it. */ }
    }

    private void log(string message) => _options.Log?.Invoke($"bobcat runner: {message}");

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cancellation;
        lock (_gate) cancellation = _inFlightCancellation;

        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }

        await drainInFlight();

        lock (_gate)
        {
            _inFlightCancellation?.Dispose();
            _inFlightCancellation = null;
        }

        _client.Dispose();
    }
}
