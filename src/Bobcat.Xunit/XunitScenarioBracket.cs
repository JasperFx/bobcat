using System.Reflection;
using Xunit;
using Xunit.v3;

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

    internal static void Open(MethodInfo methodUnderTest, IXunitTest test)
    {
        // A theory row is its own specification (bobcat#569): its arguments, or the label a
        // [BobcatExample] or a data attribute's Label gives it, are part of its identity. A [Fact]
        // has neither, and opens exactly the scenario it always did.
        MarkerStepRun.BeginScenario(methodUnderTest, Mode, test.TestMethodArguments, test.TestLabel);

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
    /// <remarks>
    /// <b>A failing Acceptance spec (bobcat#545) is reported skipped, in one of two ways.</b> When only
    /// gathered wrongs failed it, the body finished cleanly and a skip thrown here is the only exception
    /// xUnit holds, so it reads it as a skip. When the body threw, a second exception would make an
    /// aggregate that xUnit never reads as a skip, so the test is registered with
    /// <see cref="AcceptanceTestCase"/>, which rewrites its <c>TestFailed</c> on the way out.
    /// </remarks>
    internal static void Close(IXunitTest? test = null)
    {
        var verdict = BobcatScenarioAttribute.VerdictFrom(TestContext.Current.TestState);

        // CloseScenario closes the recording, which raises ScenarioCompleted, which is where the
        // reports are written — so the sink has to still be open across this call and is disposed
        // only afterwards.
        var close = MarkerStepRun.CloseScenario(verdict);

        _output.Value?.Dispose();
        _output.Value = null;

        if (close.SkipReason is { } reason)
        {
            if (verdict.Kind == ScenarioVerdictKind.Failed && test is not null)
            {
                AcceptanceTestCase.SkipAsAcceptance(test.UniqueID, reason);
                return;
            }

            throw new SkipException(reason);
        }

        if (close.Rethrow is not null) throw close.Rethrow;
    }

    /// <summary>
    /// A dynamic skip, spelled the way xUnit v3 reads one: <c>Assert.Skip</c> lives in the assertion
    /// package, which this adapter does not reference, so the message carries xUnit's documented
    /// dynamic-skip token itself.
    /// </summary>
    private sealed class SkipException(string reason) : Exception(DynamicSkipToken.Value + reason);
}
