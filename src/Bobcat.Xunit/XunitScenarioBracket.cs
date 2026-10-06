using System.Reflection;
using Xunit;

namespace Bobcat.Xunit;

/// <summary>
/// The scenario bracket both <see cref="BobcatScenarioAttribute"/> and
/// <see cref="BobcatSpecAttribute"/> run, in one place.
/// </summary>
/// <remarks>
/// Extracted when <c>[BobcatSpec]</c> arrived (issue #403). The two attributes cannot share a base
/// class — one derives from <c>BeforeAfterTestAttribute</c> and the other from
/// <see cref="FactAttribute"/> — so without this the bracket would exist twice, including the
/// gathered-wrong rethrow below, which is the subtle half.
/// </remarks>
internal static class XunitScenarioBracket
{
    internal const string Mode = "xunit";

    internal static void Open(MethodInfo methodUnderTest)
        => MarkerStepRun.BeginScenario(methodUnderTest, Mode);

    /// <remarks>
    /// <b>Throwing here is how a gathered wrong reaches the runner.</b> <see cref="SpecAssert"/>
    /// records a failed value check without throwing, so that a specification shows every
    /// disagreement rather than only its first — and a test whose checks all gathered would
    /// otherwise finish without an exception and be reported green over a red specification. xUnit
    /// folds an exception from an after-test hook into the test's own result, which is precisely
    /// the window needed: the spec's verdict lands on the test, once, at the end.
    /// </remarks>
    internal static void Close()
    {
        var gathered = MarkerStepRun.EndScenario(
            BobcatScenarioAttribute.VerdictFrom(TestContext.Current.TestState));

        if (gathered is not null) throw gathered;
    }
}
