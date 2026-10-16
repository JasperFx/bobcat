using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Bobcat.NUnit;

/// <summary>
/// Opens a Bobcat scenario around an NUnit test, so its marker comments and <c>[BobcatStep]</c>
/// helpers render as specification steps and its verdict reaches a running Bobcat console
/// (issue #385).
/// </summary>
/// <remarks>
/// <para>
/// The same bracket as the xUnit and TUnit adapters — <see cref="MarkerStepRun"/> holds everything
/// they have in common — reached through NUnit's <see cref="ITestAction"/>:
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
/// <b>On the class it covers every test in it</b>, because <see cref="Targets"/> is
/// <see cref="ActionTargets.Test"/>: NUnit runs a class-level action around each test rather than
/// once around the fixture. A single method works too.
/// </para>
/// <para>
/// <b>The verdict is the runner's.</b> <c>TestContext.CurrentContext.Result</c> is populated by the
/// time <see cref="AfterTest"/> runs, so the outcome published is the one NUnit recorded.
/// </para>
/// <para>
/// <b>Stateless on purpose.</b> NUnit builds one attribute instance per declaration and runs it
/// around every test it covers, in parallel when the suite is parallel — so the scenario lives in
/// <see cref="ScenarioRecorder"/>'s ambient slot and the output scope in an <c>AsyncLocal</c>,
/// never in a field.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class BobcatScenarioAttribute : Attribute, ITestAction
{
    // Armed when the runner instantiates the attribute, which is during discovery and so before any
    // fixture or host startup can throw (bobcat#539).
    static BobcatScenarioAttribute() => SpecEnvironment.Arm();

    internal const string Mode = "nunit";

    private static readonly AsyncLocal<IDisposable?> _output = new();

    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test)
    {
        // The method's declaring type, as the xUnit adapter uses: that is the class the generator
        // read [BobcatFeature] and the marker comments from.
        var declaringType = test.Method?.MethodInfo.DeclaringType ?? test.TypeInfo?.Type;
        MarkerStepRun.BeginScenario(declaringType, test.MethodName ?? test.Name, Mode);

        // Captured now rather than looked up per line: TestContext.Out resolves against the
        // context current at the call, and a line written from a continuation must still land on
        // this test's output.
        var writer = TestContext.Out;
        _output.Value = SpecOutput.Open(writer.WriteLine);
    }

    /// <remarks>
    /// <b>Throwing here is how a gathered wrong reaches the runner</b>, as in the xUnit adapter:
    /// NUnit records an exception from an action's <see cref="AfterTest"/> against the test it
    /// wraps, so a specification whose <c>SpecAssert</c> checks failed without throwing still
    /// turns the test red.
    /// </remarks>
    public void AfterTest(ITest test)
    {
        var result = TestContext.CurrentContext.Result;

        // Before the sink closes: EndScenario is what raises ScenarioCompleted, and that is where
        // the scenario's reports are written.
        var gathered = MarkerStepRun.EndScenario(VerdictFrom(result.Outcome, result.Message));

        _output.Value?.Dispose();
        _output.Value = null;

        if (gathered is not null) throw gathered;
    }

    /// <summary>
    /// Translate NUnit's verdict into Bobcat's. Public because it is the only part of this adapter
    /// with a decision in it, and a decision worth testing directly.
    /// </summary>
    /// <param name="outcome">NUnit's <c>ResultState</c> for the test.</param>
    /// <param name="message">
    /// NUnit's result message. For an unexpected exception NUnit writes it as
    /// <c>"Full.Type.Name : message"</c>, which is split back into its two halves so a viewer shows
    /// the same thing it shows for the other runners.
    /// </param>
    public static ScenarioVerdict VerdictFrom(ResultState? outcome, string? message)
        => outcome switch
        {
            null => ScenarioVerdict.NotClaimed,

            // A warning is a test NUnit passed: Assert.Warn records it and carries on.
            { Status: TestStatus.Passed or TestStatus.Warning } => ScenarioVerdict.Passed,

            // A cancelled run, and a test NUnit could not run at all (NotRunnable, labelled
            // "Invalid": a bad signature, a missing argument), are Failed statuses in NUnit's
            // vocabulary — but the body was stopped or never started rather than proven wrong. The
            // same call TUnit's adapter makes for Cancelled and NotStarted.
            { Status: TestStatus.Failed, Label: "Cancelled" or "Invalid" } => ScenarioVerdict.NotClaimed,

            { Status: TestStatus.Failed, Label: "Error" } => failedWithException(message),
            { Status: TestStatus.Failed } => ScenarioVerdict.Failed(null, nullIfBlank(message) ?? outcome.ToString()),

            // Skipped (Ignored, Explicit) and Inconclusive (a failed Assume) both mean the body never
            // made its assertions, and every outcome word Bobcat has would claim that it did.
            _ => ScenarioVerdict.NotClaimed
        };

    private static ScenarioVerdict failedWithException(string? message)
    {
        message = nullIfBlank(message);
        if (message is null) return ScenarioVerdict.Failed(null, "Error");

        var separator = message.IndexOf(" : ", StringComparison.Ordinal);
        return separator > 0 && !message.Substring(0, separator).Contains(' ')
            ? ScenarioVerdict.Failed(message.Substring(0, separator), message.Substring(separator + 3))
            : ScenarioVerdict.Failed(null, message);
    }

    private static string? nullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
