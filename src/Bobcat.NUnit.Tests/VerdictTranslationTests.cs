using NUnit.Framework;
using NUnit.Framework.Interfaces;
using Shouldly;

namespace Bobcat.NUnit.Tests;

/// <summary>
/// Issue #385: translating NUnit's verdict into Bobcat's is the only decision the adapter makes, so
/// it is the part worth testing directly rather than through a running suite.
/// </summary>
public class VerdictTranslationTests
{
    [Test]
    public void a_passing_test_is_a_pass()
        => BobcatScenarioAttribute.VerdictFrom(ResultState.Success, null).Kind
            .ShouldBe(ScenarioVerdictKind.Passed);

    [Test]
    public void a_warning_is_a_pass()
        // Assert.Warn records and carries on; NUnit does not call the test failed, so neither do we.
        => BobcatScenarioAttribute.VerdictFrom(ResultState.Warning, "careful").Kind
            .ShouldBe(ScenarioVerdictKind.Passed);

    [Test]
    public void an_assertion_failure_carries_its_message()
    {
        var verdict = BobcatScenarioAttribute.VerdictFrom(ResultState.Failure, "  Expected: 1\n  But was:  2\n");

        verdict.Kind.ShouldBe(ScenarioVerdictKind.Failed);
        verdict.FailureType.ShouldBeNull();
        verdict.Describe().ShouldBe("Expected: 1\n  But was:  2");
    }

    [Test]
    public void an_unexpected_exception_is_split_back_into_type_and_message()
    {
        // NUnit writes an error as "Full.Type.Name : message", and a viewer shows the same
        // "Type: message" line it shows for the other runners.
        var verdict = BobcatScenarioAttribute.VerdictFrom(
            ResultState.Error, "System.InvalidOperationException : no shard");

        verdict.Kind.ShouldBe(ScenarioVerdictKind.Failed);
        verdict.FailureType.ShouldBe("System.InvalidOperationException");
        verdict.FailureMessage.ShouldBe("no shard");
    }

    [Test]
    public void an_error_message_that_is_not_a_type_is_kept_whole()
        => BobcatScenarioAttribute.VerdictFrom(ResultState.Error, "it broke : badly").Describe()
            .ShouldBe("it broke : badly");

    [Test]
    public void a_failure_with_no_message_still_says_something()
        => BobcatScenarioAttribute.VerdictFrom(ResultState.Failure, null).Describe().ShouldBe("Failed");

    [Test]
    public void a_failure_in_teardown_is_a_failure()
        => BobcatScenarioAttribute.VerdictFrom(ResultState.TearDownError, "boom").Kind
            .ShouldBe(ScenarioVerdictKind.Failed);

    [TestCaseSource(nameof(noClaim))]
    public void a_test_that_did_not_make_its_assertions_claims_nothing(ResultState state)
        => BobcatScenarioAttribute.VerdictFrom(state, null).Kind.ShouldBe(ScenarioVerdictKind.NotClaimed);

    private static IEnumerable<ResultState> noClaim() =>
    [
        ResultState.Ignored, ResultState.Explicit, ResultState.Skipped,
        ResultState.Inconclusive, ResultState.NotRunnable, ResultState.Cancelled
    ];

    [Test]
    public void an_absent_result_claims_nothing()
        => BobcatScenarioAttribute.VerdictFrom(null, null).Kind.ShouldBe(ScenarioVerdictKind.NotClaimed);
}
