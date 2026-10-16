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
/// What every scenario opening after a <see cref="SpecCatastrophicException"/> fails with (bobcat#539):
/// the environment is gone, so the scenario is not run. Its message names where the environment was
/// lost and what the catastrophic failure said; that one failure, not this, is what to fix.
/// </summary>
/// <remarks>
/// A <see cref="SpecCatastrophicException"/> itself, so a retry policy refuses it for the same reason
/// it refuses the original. It carries no inner exception on purpose: a suite of hundreds of halted
/// tests would otherwise print the same startup stack trace hundreds of times.
/// </remarks>
public class SpecEnvironmentLostException : SpecCatastrophicException
{
    public SpecEnvironmentLostException(EnvironmentLoss loss)
        : base($"{loss.Describe()} (this scenario did not run)")
    {
        Loss = loss;
    }

    /// <summary>The catastrophic failure that lost the environment.</summary>
    public EnvironmentLoss Loss { get; }
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

/// <summary>
/// A written cell that cannot be read as the type it binds to.
/// </summary>
/// <remarks>
/// <b>Deliberately outside the <c>Spec*</c> tier vocabulary.</b> Those three words mean something to
/// the runner — assertion, critical, catastrophic — and a bad cell is none of them: it is one row's
/// problem, which <c>DecisionTableComparer.IsRowFailure</c> therefore admits so the row fails, the
/// grid shows which cell, and the remaining rows still run. A table literal only exists at run
/// time, so this is where the mistake surfaces.
/// </remarks>
public class BadCellException : Exception
{
    public BadCellException(string message) : base(message)
    {
    }

    public BadCellException(string message, Exception inner) : base(message, inner)
    {
    }
}
