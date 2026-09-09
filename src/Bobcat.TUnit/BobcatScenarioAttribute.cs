using TUnit.Core;
using TUnit.Core.Enums;
using TUnit.Core.Interfaces;

namespace Bobcat.TUnit;

/// <summary>
/// Opens a Bobcat scenario around a TUnit test, so its marker comments and <c>[BobcatStep]</c>
/// helpers render as specification steps and its verdict reaches a running Bobcat console
/// (issue #110).
/// </summary>
/// <remarks>
/// <para>
/// The same bracket as the xUnit adapter — <see cref="MarkerStepRun"/> holds everything both have
/// in common — reached through TUnit's event receivers instead of an inherited base attribute:
/// </para>
/// <code>
/// [BobcatFeature("Booking appointments"), BobcatScenario]
/// public class BookingSpecs
/// {
///     [Test]
///     public async Task a_proposed_appointment_is_confirmed() { /* Given ... */ }
/// }
/// </code>
/// <para>
/// <b>Two stages, deliberately.</b> The scenario opens <see cref="EventReceiverStage.Early"/> so
/// it is already ambient when the body — and any interceptor inside it — runs, and closes
/// <see cref="EventReceiverStage.Late"/> so the result TUnit records is the one that gets
/// published.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class BobcatScenarioAttribute : Attribute, ITestStartEventReceiver, ITestEndEventReceiver
{
    internal const string Mode = "tunit";

    EventReceiverStage ITestStartEventReceiver.Stage => EventReceiverStage.Early;

    EventReceiverStage ITestEndEventReceiver.Stage => EventReceiverStage.Late;

    public ValueTask OnTestStart(TestContext context)
    {
        var details = context.Metadata.TestDetails;
        MarkerStepRun.BeginScenario(details.ClassType, details.MethodName, Mode);
        return default;
    }

    public ValueTask OnTestEnd(TestContext context)
    {
        MarkerStepRun.EndScenario(VerdictFrom(context.Execution.Result));
        return default;
    }

    /// <summary>
    /// Translate TUnit's verdict into Bobcat's. Public because it is the only part of this adapter
    /// with a decision in it, and a decision worth testing directly.
    /// </summary>
    public static ScenarioVerdict VerdictFrom(TestResult? result)
        => result?.State switch
        {
            null => ScenarioVerdict.NotClaimed,
            TestState.Passed => ScenarioVerdict.Passed,

            // A timeout is a failure with a boring exception, not an absence of one.
            TestState.Failed or TestState.Timeout => ScenarioVerdict.FromException(result.Exception)
                is { Kind: ScenarioVerdictKind.Failed } failed
                    ? failed
                    : ScenarioVerdict.Failed(null, result.State.ToString()),

            // Skipped, cancelled, or never started: the body did not make its assertions, and
            // every outcome word Bobcat has would claim that it did.
            _ => ScenarioVerdict.NotClaimed
        };
}
