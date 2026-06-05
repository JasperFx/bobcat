namespace Bobcat.Engine;

/// <summary>
/// The ambient clock specs resolve time against (relative tokens, clock grammars, and —
/// when shared — the system under test). Backed by an <see cref="AsyncLocal{T}"/> so each
/// scenario's flow has its own clock; the test runner resets it to a fresh controllable
/// clock per scenario. Falls back to <see cref="TimeProvider.System"/> outside a test run.
/// </summary>
public static class BobcatClock
{
    private static readonly AsyncLocal<TimeProvider?> _current = new();

    public static TimeProvider Current => _current.Value ?? TimeProvider.System;

    public static void Set(TimeProvider provider) => _current.Value = provider;

    public static void Reset() => _current.Value = null;

    /// <summary>
    /// Return the current clock as a <see cref="ControllableTimeProvider"/>, installing a fresh
    /// one frozen at the current instant if the ambient clock is not already controllable.
    /// </summary>
    public static ControllableTimeProvider Controllable()
    {
        if (_current.Value is ControllableTimeProvider controllable) return controllable;
        var fresh = new ControllableTimeProvider(Current.GetUtcNow());
        _current.Value = fresh;
        return fresh;
    }

    /// <summary>
    /// Install a fresh controllable clock frozen at the real current instant. Called per
    /// scenario by the runner so time-travel never leaks between scenarios.
    /// </summary>
    public static ControllableTimeProvider ResetToControllable()
    {
        var fresh = new ControllableTimeProvider(TimeProvider.System.GetUtcNow());
        _current.Value = fresh;
        return fresh;
    }
}
