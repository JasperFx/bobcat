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
    /// This runner's id, and the address of its command stream — <c>BOBCAT_RUNNER_ID</c> when a
    /// parent set one, otherwise a fresh GUID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A parent that launches the runner owns its identity</b> (issue #397). A
    /// resident runner lives under a watch and is relaunched on every source change, so a minted
    /// id means a new runner per rebuild: a command a person pressed while the runner was
    /// rebuilding waits on an id that never comes back, the monitor's picker fills with dead
    /// runners, and a parent's status reports name an id the runner never registers under. A
    /// parent that derives one stable id from the checkout — <c>stoat runner</c> does — fixes all
    /// three by handing it over, and the runner uses what it was handed, unchanged.
    /// </para>
    /// <para>
    /// A fresh GUID stays the fallback, because a runner nobody named still has to be addressable,
    /// and re-registering is idempotent: a monitor holding a stream open to a dead process of the
    /// same id simply has it replaced by the live one's registration.
    /// </para>
    /// </remarks>
    public string RunnerId { get; init; }
        = Environment.GetEnvironmentVariable(IdVariable) is { Length: > 0 } handed
            ? handed
            : Guid.NewGuid().ToString();

    /// <summary>
    /// The variable a parent puts a stable runner id in. A <c>BOBCAT_*</c> variable because Bobcat
    /// really is the thing asking for it, exactly like <c>BOBCAT_RUN_ID</c>.
    /// </summary>
    public const string IdVariable = "BOBCAT_RUNNER_ID";

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
    /// <see cref="RunnerWire.WarmMode"/> once the suite has reported it damaged (issue #393).
    /// </summary>
    public IReadOnlyList<string> AvailableModes
        => _suite.WarmUnavailable is null
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
            log($"refused command {command.CommandId} ({refusal.Kind}): {refusal.Reason}");
            return;
        }

        await acknowledge(command.CommandId, accepted: true, refusal: null, token);

        var selection = SpecSelection.Of(command.Specs ?? []);
        var mode = command.ResolvedMode;

        // Run off the stream rather than on it, so the stream keeps being read while a run is in
        // flight — which is what lets a restart arrive mid-run and be acted on.
        var modesBefore = AvailableModes;

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

            // A run that cost this runner a mode (issue #393) re-announces itself, so the monitor
            // stops offering a person a button that will now be refused. Registration is
            // idempotent, which is what makes this safe to do mid-session.
            if (!AvailableModes.SequenceEqual(modesBefore))
            {
                log($"modes changed to {string.Join("/", AvailableModes)}: {_suite.WarmUnavailable}");
                await Register(CancellationToken.None);
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

        if (commandId.Length > 0) await acknowledge(commandId, accepted: true, refusal: null, token);

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
    /// Why this command is refused, or null to accept it. The four rules issue #390 names, in the
    /// order a person would want to hear them, each carrying the word a monitor acts on
    /// (issue #400) beside the sentence a person reads.
    /// </summary>
    private Refusal? refuse(RunCommand command)
    {
        if (Busy)
        {
            return new Refusal(RunnerRefusal.Busy, "this runner is already running a command");
        }

        if (!AvailableModes.Contains(command.ResolvedMode))
        {
            // Never silently downgraded. A person who asked for warm and got cold would read the
            // resulting wall clock as warm mode not working.
            //
            // A withdrawn warm mode answers with the damage that withdrew it (issue #393), because
            // "warm is not offered" from a runner that was offering it a minute ago is the one
            // case where the generic message explains nothing.
            if (command.ResolvedMode == RunnerWire.WarmMode
                && _suite.WarmUnavailable is { Length: > 0 } damage)
            {
                return new Refusal(
                    RunnerRefusal.UnsupportedMode,
                    $"warm mode is no longer available on this runner: {damage}");
            }

            return new Refusal(
                RunnerRefusal.UnsupportedMode,
                $"'{command.ResolvedMode}' is not a mode this runner offers "
                + $"(it offers {string.Join(", ", AvailableModes)})");
        }

        var selection = SpecSelection.Of(command.Specs ?? []);
        if (!selection.NarrowsAnything)
        {
            // Deliberately refused rather than read as "run everything": a command carrying no
            // identities is far more likely to be a mistake on the asking side than a request for
            // the whole suite, and the whole suite is what an ordinary run already does.
            return new Refusal(RunnerRefusal.Empty, "a command has to name at least one specification");
        }

        var unknown = selection.NotIn(_suite.SpecIdentities);
        if (unknown.Count > 0)
        {
            return new Refusal(
                RunnerRefusal.UnknownSpec,
                $"this runner has no specification named {string.Join(", ", unknown.Select(x => $"'{x}'"))}");
        }

        return null;
    }

    /// <summary>One refusal, in both the words it has to be said in.</summary>
    private sealed record Refusal(string Kind, string Reason);

    private Task acknowledge(string commandId, bool accepted, Refusal? refusal, CancellationToken token)
        => post(
            RunnerWire.AcknowledgedType,
            new RunnerAcknowledgement(
                _options.RunnerId, commandId, accepted, refusal?.Reason, refusal?.Kind),
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
