namespace Bobcat.Engine;

/// <summary>
/// Classifies the role of an execution step, which drives
/// automatic failure severity classification.
/// </summary>
public enum StepKind
{
    /// <summary>Data setup — exception is Critical (abort scenario)</summary>
    Given,

    /// <summary>Action under test — exception is Critical (abort scenario)</summary>
    When,

    /// <summary>Assertion — mismatch continues, exception is Critical</summary>
    Then,

    /// <summary>Fixture setup — exception is Critical</summary>
    SetUp,

    /// <summary>Fixture teardown — exception is Critical</summary>
    TearDown
}
