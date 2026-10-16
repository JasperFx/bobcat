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
/// <para>
/// <b>The run bracket closes with the test session (bobcat#470).</b> The run opens on the first
/// scenario, as it always has, and closes when TUnit reports the last test of the session, so a
/// live host serving several run requests (a warm resident command) publishes one run per request,
/// the job <c>Bobcat.Xunit</c>'s <c>ITestSessionLifetimeHandler</c> does there. It is reached
/// through TUnit.Core's own receiver, so this package still takes no platform reference. Measured on
/// TUnit 1.74.0, it arrives after the last test's <see cref="EventReceiverStage.Late"/> end
/// receiver; with tests running in parallel nothing promises that every other scenario has ended
/// by then, so the close waits until every scenario this process opened has ended. It is a receiver
/// on this attribute, so a session whose last test is not a scenario may never raise it; the
/// <c>ProcessExit</c> backstop closes that run, which is the one-shot answer anyway, and a resident
/// command only ever runs scenarios.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class BobcatScenarioAttribute : Attribute, ITestStartEventReceiver, ITestEndEventReceiver,
    ILastTestInTestSessionEventReceiver
{
    internal const string Mode = "tunit";

    EventReceiverStage ITestStartEventReceiver.Stage => EventReceiverStage.Early;

    EventReceiverStage ITestEndEventReceiver.Stage => EventReceiverStage.Late;

    /// <summary>
    /// The per-test output scope, held per async context rather than on the attribute — TUnit is
    /// free to share an attribute instance between tests, and tests run in parallel.
    /// </summary>
    private static readonly AsyncLocal<IDisposable?> _output = new();

    // The deferred session close above: scenarios open right now, and whether TUnit has said the
    // session's last test is done. Process-wide, because a session is.
    private static readonly Lock _sessionGate = new();
    private static int _openScenarios;
    private static bool _sessionEnding;

    public ValueTask OnTestStart(TestContext context)
    {
        lock (_sessionGate) _openScenarios++;

        var details = context.Metadata.TestDetails;
        // A data-driven row is its own specification (bobcat#569): its arguments are part of its
        // identity. TUnit carries no row label, so a row is always named by its arguments.
        MarkerStepRun.BeginScenario(details.ClassType, details.MethodName, Mode, details.TestMethodArguments, null);

        // TUnit has no ITestOutputHelper — it hands out a TextWriter — which is exactly why
        // SpecOutput's seam is an Action<string> and core names neither runner's types (issue #409).
        _output.Value = SpecOutput.Open(context.OutputWriter.WriteLine);

        // TUnit does not flow AsyncLocal values set in a receiver into the test unless asked
        // (bobcat#470, measured on TUnit 1.74.0): without this the recording is ambient here and
        // nowhere else, so OnTestEnd finds no scenario, publishes no verdict, and a step inside the
        // body records onto nothing.
        context.AddAsyncLocalValues();
        return default;
    }

    public ValueTask OnTestEnd(TestContext context)
    {
        // Before the sink closes: EndScenario is what raises ScenarioCompleted, and that is where
        // the scenario's reports are written.
        MarkerStepRun.EndScenario(VerdictFrom(context.Execution.Result));

        _output.Value?.Dispose();
        _output.Value = null;

        bool close;
        lock (_sessionGate)
        {
            _openScenarios = Math.Max(0, _openScenarios - 1);
            close = _sessionEnding && _openScenarios == 0;
            if (close) _sessionEnding = false;
        }

        if (close) MarkerStepRun.CloseRun();
        return default;
    }

    public ValueTask OnLastTestInTestSession(TestSessionContext current, TestContext testContext)
    {
        // A scenario still open (a parallel test whose end receiver has not run yet) is left to
        // close the run when it ends. CloseRun is idempotent either way.
        bool close;
        lock (_sessionGate)
        {
            close = _openScenarios == 0;
            _sessionEnding = !close;
        }

        if (close) MarkerStepRun.CloseRun();
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
