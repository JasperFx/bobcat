using System.Reflection;
using Xunit;
using Xunit.v3;

namespace Bobcat.Xunit;

/// <summary>
/// Opens a Bobcat scenario around an xUnit v3 test, so its marker comments and
/// <c>[BobcatStep]</c> helpers render as specification steps and its verdict reaches a running
/// Bobcat console (issue #110).
/// </summary>
/// <remarks>
/// <para>
/// Put it on a test method, or on the class to cover every test in it:
/// </para>
/// <code>
/// [BobcatFeature("Booking appointments"), BobcatScenario]
/// public class BookingSpecs
/// {
///     [Fact]
///     public async Task a_proposed_appointment_is_confirmed()
///     {
///         // Given a proposed appointment
///         ...
///     }
/// }
/// </code>
/// <para>
/// <b>The verdict is the runner's.</b> <c>TestContext.Current.TestState</c> is null in
/// <see cref="Before"/> and populated in <see cref="After"/>, which is the whole reason this
/// attribute can report a truthful outcome and a hand-rolled one generally did not.
/// </para>
/// <para>
/// <b>Stateless on purpose.</b> xUnit is free to share one attribute instance across tests, so
/// the scenario is held in <see cref="ScenarioRecorder"/>'s ambient slot — where a generated
/// interceptor has to look for it anyway — and never in a field here.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class BobcatScenarioAttribute : BeforeAfterTestAttribute
{
    internal const string Mode = "xunit";

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        => MarkerStepRun.BeginScenario(methodUnderTest, Mode);

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
        => MarkerStepRun.EndScenario(VerdictFrom(TestContext.Current.TestState));

    /// <summary>
    /// Translate xUnit's verdict into Bobcat's. Public because it is the only part of this
    /// adapter with a decision in it, and a decision worth testing directly.
    /// </summary>
    public static ScenarioVerdict VerdictFrom(TestResultState? state)
        => state?.Result switch
        {
            null => ScenarioVerdict.NotClaimed,
            TestResult.Passed => ScenarioVerdict.Passed,
            TestResult.Failed => ScenarioVerdict.Failed(
                state.ExceptionTypes?.FirstOrDefault(),
                state.ExceptionMessages?.FirstOrDefault()),

            // Skipped and NotRun both mean the body never made its assertions.
            _ => ScenarioVerdict.NotClaimed
        };
}
