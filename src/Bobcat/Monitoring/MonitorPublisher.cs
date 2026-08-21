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
