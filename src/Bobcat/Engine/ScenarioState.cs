using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Bobcat.Engine;

/// <summary>
/// The typed per-scenario blackboard behind <see cref="IStepContext.SetState{T}"/> /
/// <see cref="IStepContext.GetState{T}"/> — the shared-state contract of issue #212. One entry
/// per CLR type, alive for exactly one scenario bracket: the store is keyed by the
/// <see cref="IStepContext"/> instance, and the runner builds a fresh context per attempt, so
/// state written by one scenario (or one retry attempt) is structurally invisible to the next.
/// </summary>
/// <remarks>
/// This is what lets two grammar modules from two packages cooperate agreeing only on a capture
/// <em>type</em>: an HTTP grammar's act publishes its <c>TrackedExecution</c>, the store
/// grammar's <c>Then {event} is emitted</c> reads it, and neither references the other.
/// </remarks>
public sealed class ScenarioState
{
    private readonly Dictionary<Type, object> _entries = new();

    /// <summary>Publish <paramref name="value"/> as this scenario's <typeparamref name="T"/>, replacing any earlier one.</summary>
    public void Set<T>(T value) where T : notnull => _entries[typeof(T)] = value;

    /// <summary>
    /// The scenario's <typeparamref name="T"/>. Throws with a step-authoring diagnostic when no
    /// step in this scenario produced one — a far better failure than a silently-null field.
    /// </summary>
    public T Get<T>() where T : notnull
        => TryGet<T>(out var value)
            ? value
            : throw new InvalidOperationException(
                $"No step in this scenario produced a {typeof(T).Name} — did you mean to add a " +
                $"When … step that performs the act this state describes?");

    /// <summary>The scenario's <typeparamref name="T"/>, when a step has produced one.</summary>
    public bool TryGet<T>([NotNullWhen(true)] out T? value) where T : notnull
    {
        if (_entries.TryGetValue(typeof(T), out var entry))
        {
            value = (T)entry;
            return true;
        }

        value = default;
        return false;
    }
}

/// <summary>
/// Backs the <see cref="IStepContext"/> state default members with one <see cref="ScenarioState"/>
/// per context instance. A weak table rather than a member so that <em>every</em> implementation —
/// the engine's <see cref="SpecExecutionContext"/>, a hand-rolled fake, a future host — carries a
/// working blackboard without implementing anything, and adding the contract stayed non-breaking
/// for external <see cref="IStepContext"/> implementers. Entries die with their context, which is
/// exactly the "cleared with the scenario scope" lifetime, because the runner builds a context per
/// attempt.
/// </summary>
internal static class ScenarioStateStore
{
    private static readonly ConditionalWeakTable<IStepContext, ScenarioState> states = new();

    public static ScenarioState For(IStepContext context) => states.GetOrCreateValue(context);
}
