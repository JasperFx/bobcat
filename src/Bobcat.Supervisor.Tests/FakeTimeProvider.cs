namespace Bobcat.Supervisor.Tests;

/// <summary>
/// A clock the tests advance by hand, so stall and heartbeat behaviour is provable without
/// sleeping. Hand-rolled rather than a package: the supervisor only needs timestamps and
/// periodic timers, and full control over when a timer fires is the point.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _ticks;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
    }

    /// <summary>Timers ever created — lets a test assert nothing was scheduled at all.</summary>
    public int TimersCreated
    {
        get { lock (_gate) return _timers.Count; }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new FakeTimer(this, callback, state, dueTime, period, _ticks);
            _timers.Add(timer);
            return timer;
        }
    }

    /// <summary>
    /// Moves the clock, firing every due timer in order — synchronously, on this thread, with
    /// the clock reading the timer's own due time while its callback runs.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate) target = _ticks + by.Ticks;

        while (true)
        {
            FakeTimer? next = null;
            TimerCallback? callback = null;
            object? state = null;

            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.NextDue is { } due && due <= target &&
                        (next is null || due < next.NextDue))
                    {
                        next = timer;
                    }
                }

                _ticks = next?.NextDue ?? target;

                if (next is not null)
                {
                    (callback, state) = next.Consume();
                }
            }

            if (next is null) return;
            callback!(state);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long _period;

        public FakeTimer(
            FakeTimeProvider owner, TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period, long now)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _period = period > TimeSpan.Zero ? period.Ticks : 0;
            NextDue = dueTime >= TimeSpan.Zero ? now + dueTime.Ticks : null;
        }

        /// <summary>Absolute tick of the next firing; null when disposed or never due. Guarded by the owner's gate.</summary>
        public long? NextDue { get; private set; }

        /// <summary>Schedules the next firing and hands back what to invoke. Called under the owner's gate.</summary>
        public (TimerCallback Callback, object? State) Consume()
        {
            NextDue = _period > 0 && NextDue is { } due ? due + _period : null;
            return (_callback, _state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                _period = period > TimeSpan.Zero ? period.Ticks : 0;
                NextDue = dueTime >= TimeSpan.Zero ? _owner._ticks + dueTime.Ticks : null;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_owner._gate) NextDue = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
