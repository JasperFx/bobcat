using Shouldly;
using Xunit;
using XunitAdapter = Bobcat.Xunit.BobcatScenarioAttribute;
using TUnitAdapter = Bobcat.TUnit.BobcatScenarioAttribute;
// Both runners ship a TestResult, so every reference to one is spelled out.
using TUnitResult = TUnit.Core.TestResult;
using TUnitState = TUnit.Core.TestState;

namespace Bobcat.Xunit.Tests;

/// <summary>
/// Issue #110: translating a runner's verdict into Bobcat's is the only decision an adapter makes,
/// so it is the part worth testing directly rather than through a running suite.
/// </summary>
public class VerdictTranslationTests
{
    [Fact]
    public void a_passing_xunit_test_is_a_pass()
        => XunitAdapter.VerdictFrom(TestResultState.ForPassed(1m)).Kind
            .ShouldBe(ScenarioVerdictKind.Passed);

    [Fact]
    public void a_failing_xunit_test_carries_its_exception_across()
    {
        // The exception type and message are what a viewer shows, and xUnit hands over strings
        // rather than the exception — so this is the whole of the failure path.
        var state = TestResultState.FromException(1m, new InvalidOperationException("no shard"));

        var verdict = XunitAdapter.VerdictFrom(state);

        verdict.Kind.ShouldBe(ScenarioVerdictKind.Failed);
        verdict.FailureType.ShouldBe(typeof(InvalidOperationException).FullName);
        verdict.FailureMessage.ShouldBe("no shard");
        verdict.Describe().ShouldBe("System.InvalidOperationException: no shard");
    }

    [Fact]
    public void a_skipped_xunit_test_claims_nothing()
        => XunitAdapter.VerdictFrom(TestResultState.ForSkipped(0m)).Kind
            .ShouldBe(ScenarioVerdictKind.NotClaimed);

    [Fact]
    public void a_test_that_never_ran_claims_nothing()
        => XunitAdapter.VerdictFrom(TestResultState.ForNotRun(0m)).Kind
            .ShouldBe(ScenarioVerdictKind.NotClaimed);

    [Fact]
    public void an_absent_xunit_state_claims_nothing()
    {
        // TestState is null in Before and populated in After. An adapter reading it at the wrong
        // moment gets null, and null must never become a pass.
        XunitAdapter.VerdictFrom(null).Kind.ShouldBe(ScenarioVerdictKind.NotClaimed);
    }

    [Fact]
    public void a_passing_tunit_test_is_a_pass()
        => TUnitAdapter.VerdictFrom(tunitResult(TUnitState.Passed)).Kind
            .ShouldBe(ScenarioVerdictKind.Passed);

    [Fact]
    public void a_failing_tunit_test_carries_its_exception_across()
    {
        var verdict = TUnitAdapter.VerdictFrom(
            tunitResult(TUnitState.Failed, new InvalidOperationException("no shard")));

        verdict.Kind.ShouldBe(ScenarioVerdictKind.Failed);
        verdict.Describe().ShouldBe("System.InvalidOperationException: no shard");
    }

    [Fact]
    public void a_tunit_timeout_is_a_failure_even_with_no_exception()
    {
        // A timeout is a failure with a boring cause, not an absence of one. Without this it
        // would fall through to NotClaimed and a hung test would vanish from the report.
        var verdict = TUnitAdapter.VerdictFrom(tunitResult(TUnitState.Timeout));

        verdict.Kind.ShouldBe(ScenarioVerdictKind.Failed);
        verdict.Describe().ShouldBe("Timeout");
    }

    [Theory]
    [InlineData(TUnitState.Skipped)]
    [InlineData(TUnitState.Cancelled)]
    [InlineData(TUnitState.NotStarted)]
    public void a_tunit_test_that_did_not_finish_claims_nothing(TUnitState state)
        => TUnitAdapter.VerdictFrom(tunitResult(state)).Kind
            .ShouldBe(ScenarioVerdictKind.NotClaimed);

    [Fact]
    public void an_absent_tunit_result_claims_nothing()
        => TUnitAdapter.VerdictFrom(null).Kind.ShouldBe(ScenarioVerdictKind.NotClaimed);

    /// <summary>
    /// TUnit's TestResult declares every member required, so even a two-field fixture has to fill
    /// the rest in.
    /// </summary>
    private static TUnitResult tunitResult(TUnitState state, Exception? exception = null)
        => new()
        {
            State = state,
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow,
            Duration = TimeSpan.Zero,
            Exception = exception,
            ComputerName = "test"
        };
}
