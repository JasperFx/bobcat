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

    /// <summary>
    /// The per-test output sink opened alongside the scenario, so the adapter can close both.
    /// </summary>
    /// <remarks>
    /// <c>AsyncLocal</c>-held in <see cref="SpecOutput"/> itself; this only keeps the scope so
    /// <see cref="Close"/> can dispose it. Held per async context rather than on the attribute,
    /// which xUnit is free to share between tests.
    /// </remarks>
    private static readonly AsyncLocal<IDisposable?> _output = new();

    internal static void Open(MethodInfo methodUnderTest)
    {
        MarkerStepRun.BeginScenario(methodUnderTest, Mode);

        // TestOutputHelper is ambient in xUnit v3, which is what lets a [BobcatSpec] test get this
        // without declaring an ITestOutputHelper constructor parameter — the one-attribute,
        // no-ceremony goal of issue #403 would be spent by requiring one.
        if (TestContext.Current.TestOutputHelper is { } helper)
        {
            _output.Value = SpecOutput.Open(helper.WriteLine);
        }
    }

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
        // EndScenario closes the recording, which raises ScenarioCompleted, which is where the
        // reports are written — so the sink has to still be open across this call and is disposed
        // only afterwards.
        var gathered = MarkerStepRun.EndScenario(
            BobcatScenarioAttribute.VerdictFrom(TestContext.Current.TestState));

        _output.Value?.Dispose();
        _output.Value = null;

        if (gathered is not null) throw gathered;
    }
}
