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

/// <summary>
/// Signals a misconfigured Bobcat test run (wiring footguns) — surfaced with actionable
/// guidance instead of a cryptic downstream or native failure.
/// </summary>
public class BobcatConfigurationException : Exception
{
    public BobcatConfigurationException(string message) : base(message) { }
    public BobcatConfigurationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Throw from a step to signal an <b>assertion</b> failure from plain code — the step is marked
/// <see cref="ResultStatus.failed"/> at <see cref="FailureLevel.Assertion"/> and the scenario
/// continues, exactly as a <c>[Check]</c> returning false or a failed cell comparison would.
/// Any other exception escaping a step is <see cref="FailureLevel.Critical"/> and aborts the
/// scenario. This is what lets a typed assertion helper (<c>ThenEvents(...)</c>,
/// <c>ThenDocument&lt;T&gt;(...)</c>) keep "assertion failures accumulate, action failures stop"
/// semantics without the step having to return a bool.
/// </summary>
public class SpecAssertionException : Exception
{
    public SpecAssertionException(string message) : base(message) { }
    public SpecAssertionException(string message, Exception inner) : base(message, inner) { }
}
