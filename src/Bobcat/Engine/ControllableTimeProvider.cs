namespace Bobcat.Engine;

/// <summary>
/// A <see cref="TimeProvider"/> whose "now" is set explicitly and frozen until changed —
/// for deterministic time in specs. Successor to Storyteller's hand-rolled ISystemTime.
/// </summary>
public sealed class ControllableTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ControllableTimeProvider(DateTimeOffset start) => _utcNow = start;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    /// <summary>Freeze the clock at a new instant.</summary>
    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;

    /// <summary>Move the frozen clock forward (or back) by a span.</summary>
    public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
}

/// <summary>
/// A <see cref="TimeProvider"/> that always delegates to the ambient <see cref="BobcatClock.Current"/>.
/// Registered into a system-under-test's DI by <c>ShareClock()</c> so the app and the spec
/// agree on time even as the spec advances the clock between steps.
/// </summary>
public sealed class AmbientClockTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => BobcatClock.Current.GetUtcNow();
}
