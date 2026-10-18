namespace Bobcat.Runtime;

/// <summary>
/// What a <see cref="Preflight"/> found: every check that passed, and every one that threw.
/// </summary>
/// <remarks>
/// Bobcat's own since bobcat#744, shaped like the <c>JasperFx.Environment.EnvironmentCheckResults</c> it
/// replaced (same member names, <see cref="Succeeded"/> still a method) so a caller moving across changes a
/// type name and nothing else. The JasperFx container checks (<c>AddContainerChecks</c>) moved to
/// WolverineFx.Bobcat with the rest of the Critter Stack coupling.
/// </remarks>
public sealed class PreflightResults
{
    private readonly List<string> _successes = new();
    private readonly List<PreflightFailure> _failures = new();

    /// <summary>The description of every check that passed, in the order they ran.</summary>
    public string[] Successes => _successes.ToArray();

    /// <summary>Every check that threw, in the order they ran.</summary>
    public PreflightFailure[] Failures => _failures.ToArray();

    public void RegisterSuccess(string description) => _successes.Add(description);

    public void RegisterFailure(string description, Exception exception)
        => _failures.Add(new PreflightFailure(description, exception));

    /// <summary>Whether every check passed. A preflight with no checks has succeeded.</summary>
    public bool Succeeded() => _failures.Count == 0;

    /// <summary>
    /// Throw a <see cref="PreflightException"/> naming every failed check when there was one; return
    /// quietly otherwise.
    /// </summary>
    public void Assert()
    {
        if (_failures.Count > 0) throw new PreflightException(this);
    }

    public override string ToString()
        => "Environment check failures:" + Environment.NewLine
           + string.Join(Environment.NewLine, _failures.Select(x => $"{x.Description}: {x.Exception.Message}"));
}

/// <summary>One check that threw: what it was checking, and why it failed.</summary>
public sealed record PreflightFailure(string Description, Exception Exception);

/// <summary>
/// Thrown by <see cref="PreflightResults.Assert"/>: one exception for the whole preflight, listing every
/// failed check in its message and carrying each one's exception as an inner exception.
/// </summary>
public sealed class PreflightException(PreflightResults results)
    : AggregateException(results.ToString(), results.Failures.Select(x => x.Exception))
{
    /// <summary>The results that failed.</summary>
    public PreflightResults Results { get; } = results;
}
