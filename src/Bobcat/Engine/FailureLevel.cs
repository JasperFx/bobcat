namespace Bobcat.Engine;

/// <summary>
/// Severity of a step failure, driving abort/continue decisions.
/// </summary>
public enum FailureLevel
{
    /// <summary>No failure</summary>
    None,

    /// <summary>Assertion mismatch — continue executing remaining steps</summary>
    Assertion,

    /// <summary>Infrastructure failure — abort this scenario, proceed to next</summary>
    Critical,

    /// <summary>System-level failure — stop entire suite</summary>
    Catastrophic
}
