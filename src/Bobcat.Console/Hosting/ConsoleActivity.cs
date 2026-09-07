namespace Bobcat.Console.Hosting;

/// <summary>
/// The console's liveness: when it last did anything for anybody, and whether it is doing
/// something right now. Fed by one middleware at the top of the pipeline, read by
/// <see cref="IdleShutdownService"/>.
/// </summary>
/// <remarks>
/// "In flight" is the half that makes an idle ceiling safe to turn on by default. A browser with
/// the dashboard open holds a SignalR connection, which is one request that never completes — so
/// a watched console reads as busy however long it sits between runs, and the case an idle
/// ceiling must not break is the case it cannot break.
/// </remarks>
public sealed class ConsoleActivity
{
    private readonly TimeProvider _time;
    private long _lastTicks;
    private int _inFlight;

    public ConsoleActivity(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastTicks = _time.GetUtcNow().UtcTicks;
    }

    /// <summary>Requests currently being served, WebSocket connections included.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    public DateTimeOffset LastActivity => new(Volatile.Read(ref _lastTicks), TimeSpan.Zero);

    /// <summary>How long this console has done nothing for anybody. Zero while anything is in flight.</summary>
    public TimeSpan IdleFor => InFlight > 0 ? TimeSpan.Zero : _time.GetUtcNow() - LastActivity;

    public void Began()
    {
        Interlocked.Increment(ref _inFlight);
        Touch();
    }

    public void Ended()
    {
        Interlocked.Decrement(ref _inFlight);
        Touch();
    }

    public void Touch() => Volatile.Write(ref _lastTicks, _time.GetUtcNow().UtcTicks);
}
