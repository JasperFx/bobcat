namespace Bobcat.Engine.Verification;

/// <summary>
/// The outcome of a single typed value comparison.
/// </summary>
public enum CheckOutcome
{
    /// <summary>Actual matched expected.</summary>
    Match,

    /// <summary>Actual did not match expected.</summary>
    Mismatch,

    /// <summary>The expected text could not be interpreted as the target type.</summary>
    Invalid
}
