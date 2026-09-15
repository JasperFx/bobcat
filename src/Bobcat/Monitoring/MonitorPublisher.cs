using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Bobcat.Monitoring;

/// <summary>
/// Where <see cref="MonitorPublishingObserver"/> drops its events. The HTTP transport lives in
/// <see cref="MonitorPublisher"/>; tests substitute a recording sink.
/// </summary>
public interface IMonitorEventSink
{
    /// <summary>Enqueue an event. Must never block and never throw.</summary>
    void Post(MonitorEvent @event);
}

/// <summary>
/// Fire-and-forget HTTP publisher for the Bobcat.Console host. The invariant that outranks
/// everything else here: <b>a test run is never slowed or failed by the monitor.</b> Concretely:
/// <list type="bullet">
/// <item><see cref="TryConnect"/> probes <c>/api/ping</c> once with a tight timeout and returns
/// null when nothing answers — the run then proceeds with no publisher at all.</item>
/// <item>Events go into a bounded channel and are dropped on backpressure rather than blocking
/// the caller.</item>
/// <item>Repeated send failures mid-run mark the monitor gone and the pump stops; nothing is
/// retried, nothing surfaces to the run.</item>
/// </list>
/// </summary>
public sealed class MonitorPublisher : IMonitorEventSink, IAsyncDisposable
{
    public const string DefaultUrl = "http://localhost:5525";
    public const string UrlVariable = "BOBCAT_MONITOR_URL";
    public const string KillSwitchVariable = "BOBCAT_MONITOR";

    private const int channelCapacity = 2000;
    private const int maxBatchSize = 200;
    private const int consecutiveFailuresBeforeGivingUp = 3;
    private static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly Channel<MonitorEvent> _channel;
    private readonly Task _pump;
    private int _consecutiveFailures;

    private MonitorPublisher(HttpClient client)
    {
        _client = client;
        _channel = Channel.CreateBounded<MonitorEvent>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        _pump = Task.Run(pump);
    }

    /// <summary>The target URL: <c>BOBCAT_MONITOR_URL</c> when set, else the default port.</summary>
    public static string ResolveUrl()
        => Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } url ? url : DefaultUrl;

    /// <summary>Hard opt-out: <c>BOBCAT_MONITOR=0</c> (or off/false) suppresses even the probe.</summary>
    public static bool Disabled
        => Environment.GetEnvironmentVariable(KillSwitchVariable)?.ToLowerInvariant() is "0" or "off" or "false";

    /// <summary>
    /// Probes the monitor once; returns a live publisher, or null when the monitor is absent,
    /// slow, or disabled. The cost of an absent monitor is exactly one refused local connection.
    /// </summary>
    public static async Task<MonitorPublisher?> TryConnect(string? url = null, TimeSpan? probeTimeout = null)
    {
        if (Disabled) return null;

        var client = new HttpClient { BaseAddress = new Uri(url ?? ResolveUrl()) };
        try
        {
            using var cts = new CancellationTokenSource(probeTimeout ?? TimeSpan.FromMilliseconds(250));
            using var response = await client.GetAsync("/api/ping", cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                client.Dispose();
                return null;
            }
        }
        catch
        {
            client.Dispose();
            return null;
        }

        return new MonitorPublisher(client);
    }

    public void Post(MonitorEvent @event) => _channel.Writer.TryWrite(@event);

    /// <summary>Where this publisher is pointed — for a message that has to name the console.</summary>
    internal string Url => _client.BaseAddress?.ToString().TrimEnd('/') ?? ResolveUrl();

    /// <summary>
    /// The name of the Event Model the console currently serves, or null when it serves none and
    /// when anything at all goes wrong reading it. Issue #294 — <c>GET /api/event-model</c> merges
    /// only the sources naming the current model, so a producer has to know that name before it
    /// can tell "joining the other half" from "hiding it".
    /// </summary>
    /// <remarks>
    /// Deliberately reads only the name out of the document rather than deserializing the whole
    /// descriptor: the merge is a moving shape upstream, and a producer that cannot answer one
    /// string because a slice grew a field is a producer that stops publishing for no reason.
    /// </remarks>
    internal async Task<string?> CurrentEventModelName(CancellationToken token)
    {
        try
        {
            using var response = await _client.GetAsync(SpecEventModelPublisher.Route, token);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            // The console serializes camelCase; "Name" is here for a producer that pushed
            // PascalCase before the store normalized it, which costs one dictionary probe.
            foreach (var property in new[] { "name", "Name" })
            {
                if (document.RootElement.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }

            return null;
        }
        catch
        {
            // No model, no console, no answer — all the same thing to a caller that only wants
            // to know whether it is about to collide with a name someone else published.
            return null;
        }
    }

    /// <summary>
    /// What became of one <see cref="PublishEventModel"/>. Three outcomes, not two, and the
    /// distinction is load-bearing: a <b>dropped</b> push (the console went away, the ceiling
    /// expired) is silent by the invariant at the top of this file, while a <b>refused</b> one —
    /// the console answered, and said no — is the single case a human can fix. Collapsing the
    /// two, which an earlier draft of this did by returning just a nullable reason, made a
    /// dropped push read as a published one.
    /// </summary>
    internal readonly record struct EventModelPush(bool Published, string? Refusal);

    /// <summary>
    /// <c>PUT /api/event-model/{source}</c> — publish one producer's half of the model, replacing
    /// whatever that source published before. Issue #294. Never throws.
    /// </summary>
    internal async Task<EventModelPush> PublishEventModel(string source, string json, CancellationToken token)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _client.PutAsync(
                $"{SpecEventModelPublisher.Route}/{Uri.EscapeDataString(source)}", content, token);

            if (response.IsSuccessStatusCode) return new EventModelPush(Published: true, Refusal: null);

            var detail = await response.Content.ReadAsStringAsync(token);
            return new EventModelPush(
                Published: false,
                Refusal: $"the console at {Url} answered {(int)response.StatusCode} — {detail}");
        }
        catch
        {
            // Same rule as the event pump: a monitor that will not take a push is never a run's
            // problem. Nothing is retried and nothing surfaces.
            return new EventModelPush(Published: false, Refusal: null);
        }
    }

    private async Task pump()
    {
        var batch = new List<MonitorEvent>();
        while (await _channel.Reader.WaitToReadAsync())
        {
            batch.Clear();
            while (batch.Count < maxBatchSize && _channel.Reader.TryRead(out var e))
            {
                batch.Add(e);
            }

            await send(batch);

            if (_consecutiveFailures >= consecutiveFailuresBeforeGivingUp)
            {
                // The monitor went away mid-run. Stop pumping; Post keeps accepting (and
                // dropping) events so callers never notice.
                _channel.Writer.TryComplete();
                return;
            }
        }
    }

    private async Task send(IReadOnlyList<MonitorEvent> batch)
    {
        try
        {
            var json = JsonSerializer.Serialize(new IngestBatch(batch), serializerOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync("/api/ingest", content);
            _consecutiveFailures = response.IsSuccessStatusCode ? 0 : _consecutiveFailures + 1;
        }
        catch
        {
            _consecutiveFailures++;
        }
    }

    private record IngestBatch(IReadOnlyList<MonitorEvent> Events);

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        // Best-effort flush with a hard ceiling — closing out a run must not hang on a
        // wedged monitor.
        await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(2)));
        _client.Dispose();
    }
}
