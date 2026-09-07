namespace Bobcat.Console.Hosting;

/// <summary>
/// The console stops itself after a stretch with nothing connected and nothing publishing
/// (issue #200).
/// </summary>
/// <remarks>
/// A <c>bobcat run</c> was found alive <b>20h52m</b> after the session that started it had ended,
/// wedged on a port, failing the next repository's gate with <c>AddressInUseException</c> — and
/// the red read as a product regression until <c>lsof</c> named the squatter. Nothing inside the
/// process could ever have ended it: JasperFx's <c>run</c> blocks on an untimed
/// <c>ManualResetEventSlim</c> whose only realistic release is a Ctrl-C that a process detached
/// from a dead terminal never receives.
///
/// A parent-death watchdog is the obvious answer and is not portable — .NET has no
/// cross-platform "who is my parent" — and watching for stdin to close kills a legitimately
/// backgrounded console the moment it starts. Idleness is the honest signal, because it is a
/// statement about this process's <em>purpose</em>: a viewer with no browser attached and no run
/// publishing to it is doing nothing for anybody, and is only holding its port against whoever
/// wants it next.
///
/// On by default, unlike the retry and stall knobs elsewhere in Bobcat, because those preserve a
/// behaviour someone may be relying on and this one preserves nothing. Two hours is generous
/// enough that a developer who leaves the console open all day never meets it: any request at all
/// resets the window, and an open dashboard tab never even starts it.
/// </remarks>
public sealed class IdleShutdownService : BackgroundService
{
    /// <summary>Environment fallback for <c>Monitor:IdleMinutes</c>. Zero or negative disables the ceiling.</summary>
    public const string IdleVariable = "BOBCAT_MONITOR_IDLE_MINUTES";

    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromHours(2);

    private readonly ConsoleActivity _activity;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IdleShutdownService> _logger;
    private readonly TimeSpan _timeout;

    public IdleShutdownService(ConsoleActivity activity, IHostApplicationLifetime lifetime,
        ILogger<IdleShutdownService> logger, IConfiguration configuration)
        : this(activity, lifetime, logger, IdleTimeoutFrom(configuration))
    {
    }

    public IdleShutdownService(ConsoleActivity activity, IHostApplicationLifetime lifetime,
        ILogger<IdleShutdownService> logger, TimeSpan timeout)
    {
        _activity = activity;
        _lifetime = lifetime;
        _logger = logger;
        _timeout = timeout;
    }

    /// <summary><c>Monitor:IdleMinutes</c>, then <see cref="IdleVariable"/>, then two hours.</summary>
    public static TimeSpan IdleTimeoutFrom(IConfiguration configuration)
    {
        var minutes = configuration.GetValue<double?>("Monitor:IdleMinutes") ?? fromEnvironment();
        return minutes is { } value ? TimeSpan.FromMinutes(value) : DefaultIdleTimeout;
    }

    /// <summary>
    /// One check, deliberately separate from the timer so the decision is provable without a
    /// clock. Returns true when it asked the host to stop.
    /// </summary>
    public bool StopIfIdle()
    {
        var idle = _activity.IdleFor;
        if (_timeout <= TimeSpan.Zero || idle < _timeout) return false;

        // Loud, and it says what to change: the next person to meet this is someone who WANTED a
        // console running unattended, and the message is the only place they will learn how.
        _logger.LogWarning(
            "Shutting this Bobcat console down: nothing has connected to it and no run has published to it for {Idle}. " +
            "A console nobody is watching only holds its port against the next process that wants it (issue #200). " +
            "Set Monitor:IdleMinutes or {Variable} to change the window — 0 runs until stopped.",
            idle, IdleVariable);

        _lifetime.StopApplication();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_timeout <= TimeSpan.Zero)
        {
            _logger.LogDebug("Idle shutdown is off; this console runs until it is stopped.");
            return;
        }

        // Often enough that the reported idle time is roughly true, rarely enough to cost nothing.
        var period = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond * 5, _timeout.Ticks / 20));
        using var timer = new PeriodicTimer(period);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (StopIfIdle()) return;
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping for its own reasons; nothing to say about it.
        }
    }

    private static double? fromEnvironment()
        => double.TryParse(Environment.GetEnvironmentVariable(IdleVariable), out var minutes) ? minutes : null;
}
