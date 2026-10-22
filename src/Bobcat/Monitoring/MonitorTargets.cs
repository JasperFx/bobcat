namespace Bobcat.Monitoring;

/// <summary>
/// The monitor targets one run publishes to — <c>BOBCAT_MONITOR_URL</c> read as a list
/// (bobcat#530). Every event goes to every target that answered the probe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a list.</b> Two consumers now want the same events and only one of them can hold port
/// 5525: the run console (<c>bobcat ui</c>, bobcat#524) keeps it, and Stoat listens on its own port
/// so it can show a gate's live progress while the suite runs. Forwarding from one console to the
/// other was the alternative and was decided against — it would make Stoat's live view depend on
/// the run console running.
/// </para>
/// <para>
/// <b>Isolation is structural, not promised.</b> This type holds one <see cref="MonitorPublisher"/>
/// per URL, and a publisher already owns its client, its bounded channel, its drop-on-backpressure
/// policy and its give-up counter. So <i>"a test run is never slowed or failed by the monitor"</i>
/// holds per target with nothing added: a target that is unreachable, slow, wedged or hostile
/// cannot cost any other target one event, and cannot cost the run anything at all. The one place
/// that could have become shared — the probe — is run concurrently, so N absent consoles cost one
/// probe timeout rather than N.
/// </para>
/// <para>
/// <b>Absent means absent.</b> A target that did not answer is not held open for later: the run
/// proceeds with the ones that did, and with none at all when nobody answered (<c>TryConnect</c>
/// hands back null, exactly as the single-target probe did). Nothing is retried mid-run, here or
/// in a publisher.
/// </para>
/// </remarks>
public sealed class MonitorTargets : IMonitorEventSink, IAsyncDisposable
{
    private readonly IReadOnlyList<MonitorPublisher> _publishers;

    private MonitorTargets(IReadOnlyList<MonitorPublisher> publishers) => _publishers = publishers;

    /// <summary>
    /// Probe every target once — concurrently — and return the live ones, or null when none
    /// answered or the monitor is disabled outright.
    /// </summary>
    /// <param name="urls">
    /// The targets; null reads <see cref="MonitorPublisher.ResolveUrls"/>, which is the
    /// environment's answer and is never empty.
    /// </param>
    /// <param name="probeTimeout">Per target, as <see cref="MonitorPublisher.TryConnect"/> applies it.</param>
    public static async Task<MonitorTargets?> TryConnect(
        IEnumerable<string>? urls = null, TimeSpan? probeTimeout = null)
    {
        if (MonitorPublisher.Disabled) return null;

        var targets = (urls ?? MonitorPublisher.ResolveUrls()).ToArray();
        if (targets.Length == 0) return null;

        // Concurrently, so the cost of N absent consoles is one probe timeout rather than N. The
        // order of the results is the order of the list, which is the order events are posted in.
        var probes = await Task.WhenAll(targets.Select(url => MonitorPublisher.TryConnect(url, probeTimeout)));

        var live = probes.OfType<MonitorPublisher>().ToArray();
        return live.Length == 0 ? null : new MonitorTargets(live);
    }

    /// <summary>The live targets, in the order the list named them.</summary>
    internal IReadOnlyList<MonitorPublisher> Publishers => _publishers;

    /// <summary>Where this run is publishing — for a message, or a test, that has to name them.</summary>
    public IReadOnlyList<string> Urls => _publishers.Select(p => p.Url).ToArray();

    /// <summary>
    /// Enqueue on every target. Never blocks and never throws, because
    /// <see cref="MonitorPublisher.Post"/> does neither and each target has its own channel.
    /// </summary>
    public void Post(MonitorEvent @event)
    {
        foreach (var publisher in _publishers) publisher.Post(@event);
    }

    /// <summary>
    /// Enqueue a terminal record on every target (bobcat#767) — each stamps its own drop count,
    /// because each has its own queue and its own drops.
    /// </summary>
    public void PostTerminal(MonitorEvent @event)
    {
        foreach (var publisher in _publishers) publisher.PostTerminal(@event);
    }

    /// <summary>
    /// One sentence naming every target that dropped events under backpressure, and how many —
    /// or null when none did. For a log line at the end of a run; the same figure reaches each
    /// console on its own <c>run_finished</c>.
    /// </summary>
    public string? DropReport()
    {
        var drops = _publishers.Where(p => p.Dropped > 0).Select(p => $"{p.Dropped} to {p.Url}").ToList();
        if (drops.Count == 0) return null;

        return "monitor events dropped under backpressure: " + string.Join(", ", drops) +
               " — per-test progress on those consoles may be incomplete; run_finished carries the final counts";
    }

    /// <summary>
    /// Flush every target concurrently. Each publisher already caps its own wait, and running them
    /// together keeps a run's close-out at one ceiling rather than one per target.
    /// </summary>
    public async ValueTask DisposeAsync()
        => await Task.WhenAll(_publishers.Select(p => p.DisposeAsync().AsTask()));
}
