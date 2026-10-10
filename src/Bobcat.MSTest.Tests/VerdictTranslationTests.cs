using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Bobcat.MSTest.Tests;

/// <summary>
/// Issue #386: translating MSTest's verdict into Bobcat's is the only decision the adapter makes.
/// The outcome arrives by NAME, because Bobcat.MSTest references no MSTest major — so the names
/// are checked against the real enum here, where one is in hand.
/// </summary>
[TestClass]
public class VerdictTranslationTests
{
    [TestMethod]
    public void a_passing_test_is_a_pass()
        => MSTestScenarioBracket.VerdictFrom(nameof(UnitTestOutcome.Passed), null).Kind
            .ShouldBe(ScenarioVerdictKind.Passed);

    [TestMethod]
    public void a_failing_test_carries_its_exception_across()
    {
        var verdict = MSTestScenarioBracket.VerdictFrom(
            nameof(UnitTestOutcome.Failed), new InvalidOperationException("no shard"));

        verdict.Kind.ShouldBe(ScenarioVerdictKind.Failed);
        verdict.Describe().ShouldBe("System.InvalidOperationException: no shard");
    }

    [TestMethod]
    public void the_invocation_wrapper_is_looked_through()
    {
        var verdict = MSTestScenarioBracket.VerdictFrom(
            nameof(UnitTestOutcome.Error),
            new System.Reflection.TargetInvocationException(new InvalidOperationException("no shard")));

        verdict.Describe().ShouldBe("System.InvalidOperationException: no shard");
    }

    [TestMethod]
    public void a_timeout_is_a_failure_even_with_no_exception()
    {
        // Without this a hung test would fall through to NotClaimed and vanish from the report.
        var verdict = MSTestScenarioBracket.VerdictFrom(nameof(UnitTestOutcome.Timeout), null);

        verdict.Kind.ShouldBe(ScenarioVerdictKind.Failed);
        verdict.Describe().ShouldBe("Timeout");
    }

    [TestMethod]
    [DataRow(nameof(UnitTestOutcome.Inconclusive))]
    [DataRow(nameof(UnitTestOutcome.Ignored))]
    [DataRow(nameof(UnitTestOutcome.Aborted))]
    [DataRow(nameof(UnitTestOutcome.NotRunnable))]
    [DataRow(nameof(UnitTestOutcome.NotFound))]
    [DataRow(nameof(UnitTestOutcome.InProgress))]
    [DataRow(nameof(UnitTestOutcome.Unknown))]
    [DataRow(null)]
    public void a_test_that_did_not_finish_claims_nothing(string? outcome)
        => MSTestScenarioBracket.VerdictFrom(outcome, null).Kind.ShouldBe(ScenarioVerdictKind.NotClaimed);

    [TestMethod]
    public void every_outcome_mstest_has_is_one_the_translation_has_decided()
    {
        // A new UnitTestOutcome would otherwise fall silently into NotClaimed. Listing them here
        // means adding one to MSTest fails this until someone decides what it means.
        Enum.GetNames<UnitTestOutcome>().Order(StringComparer.Ordinal).ShouldBe(
        [
            "Aborted", "Error", "Failed", "Ignored", "InProgress", "Inconclusive", "NotFound",
            "NotRunnable", "Passed", "Timeout", "Unknown"
        ]);
    }
}
