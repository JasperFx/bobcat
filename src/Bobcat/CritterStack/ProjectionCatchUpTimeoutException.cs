namespace Bobcat.CritterStack;

/// <summary>
/// bobcat#497. The wait for the async projections to catch up, before a read such as
/// <see cref="CritterStackScenario.ThenDocument{T}(object)" />, ran out of time. An <b>error</b>, not a specification
/// failure: a daemon that is behind, stopped or not running is the environment, not evidence that the document is
/// wrong. The message names the wait, the projections that were behind (when the store can say), the timeout and
/// what was being read; the store's own exception is the <see cref="Exception.InnerException" />.
/// </summary>
/// <remarks>
/// A <see cref="TimeoutException" />, so code that caught the store's timeout before still catches this one.
/// </remarks>
public sealed class ProjectionCatchUpTimeoutException : TimeoutException
{
    public ProjectionCatchUpTimeoutException(string wait, IReadOnlyList<string> behind, TimeSpan timeout, Type readType,
        object? identity, Exception inner)
        : base(describe(wait, behind, timeout, readType, identity, inner), inner)
    {
        Wait = wait;
        Behind = behind;
        Timeout = timeout;
        ReadType = readType;
        Identity = identity;
    }

    /// <summary>The wait that timed out, as the step that ran it reads.</summary>
    public string Wait { get; }

    /// <summary>The projection shards that had not caught up, as <c>'shard' at N of M</c>; empty when the store could not say.</summary>
    public IReadOnlyList<string> Behind { get; }

    /// <summary>How long the wait was given (<see cref="CritterStackScenario.ProjectionTimeout" />).</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The type the read after the wait was for.</summary>
    public Type ReadType { get; }

    /// <summary>The identity being read, or null for a read with none (every document of a type, the scenario's stream).</summary>
    public object? Identity { get; }

    private static string describe(string wait, IReadOnlyList<string> behind, TimeSpan timeout, Type readType, object? identity,
        Exception inner)
    {
        var reading = identity is null ? $"the {readType.Name}" : $"the {readType.Name} with id {identity}";
        var shards = behind.Count == 0
            ? "The store could not say which projection was behind."
            : $"Behind: {string.Join(", ", behind)}.";
        return $"Waiting for '{wait}' timed out after {timeout.TotalSeconds:0.###}s, before reading {reading}. {shards} "
               + "This is the environment (a daemon behind, stopped or not running), not evidence about the "
               + $"{readType.Name} itself. The store said: {inner.GetType().Name}: {inner.Message}";
    }
}
