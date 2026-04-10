namespace Bobcat.Engine;

/// <summary>
/// Throw from a step to signal that the current scenario should be aborted
/// but subsequent scenarios should continue.
/// </summary>
public class SpecCriticalException : Exception
{
    public SpecCriticalException(string message) : base(message) { }
    public SpecCriticalException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Throw from a step to signal that the entire test suite should stop.
/// Use for unrecoverable infrastructure failures (host won't start, database gone, etc).
/// </summary>
public class SpecCatastrophicException : Exception
{
    public SpecCatastrophicException(string message) : base(message) { }
    public SpecCatastrophicException(string message, Exception inner) : base(message, inner) { }
}
