using System.Reflection;

namespace Bobcat.MSTest;

/// <summary>
/// The scenario bracket for an MSTest suite, run around every test by the global hooks this
/// package compiles into the test project.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why global hooks, and not the two shapes issue #386 started from.</b> Subclassing
/// <c>TestMethodAttribute</c> hands over the result directly, but it replaces <c>[TestMethod]</c>,
/// and MSTest 4 renamed the method it overrides (<c>Execute</c> became <c>ExecuteAsync</c>). A
/// <c>[TestCleanup]</c> needs a base class or a partial. MSTest 3.10's
/// <c>[GlobalTestInitialize]</c>/<c>[GlobalTestCleanup]</c> run around every test in the assembly
/// with the test's <c>TestContext</c>, which is all a bracket needs: the identity going in, the
/// outcome coming out, and no edit to any test.
/// </para>
/// <para>
/// <b>The hooks must be in the test assembly</b> — MSTest only looks for them there — so the package
/// ships them as source (<c>buildTransitive/BobcatMSTestHooks.cs</c>). That has a second payoff,
/// and it is why this assembly takes <b>no reference to MSTest at all</b>: MSTest 4 also renamed its
/// assemblies, so anything compiled against one major fails to load under the other. The hook
/// source compiles against whichever MSTest the consumer has and hands this class plain values.
/// </para>
/// <para>
/// Opt out with <c>&lt;BobcatMSTestHooks&gt;false&lt;/BobcatMSTestHooks&gt;</c> and call
/// <see cref="Open"/>/<see cref="Close"/> from hooks of your own.
/// </para>
/// <para>
/// <b>Stateless on purpose.</b> MSTest may run tests in parallel, so the scenario lives in
/// <see cref="ScenarioRecorder"/>'s ambient slot and the output scope in an <c>AsyncLocal</c>;
/// MSTest flows the execution context from the global initialize into the test body.
/// </para>
/// </remarks>
public static class MSTestScenarioBracket
{
    internal const string Mode = "mstest";

    private static readonly AsyncLocal<IDisposable?> _output = new();

    /// <summary>
    /// Open a scenario for a test, if its class or method carries <see cref="BobcatScenarioAttribute"/>.
    /// </summary>
    /// <param name="testAssembly">
    /// The assembly the test lives in. MSTest names the class only as a string, so this is where it
    /// is resolved.
    /// </param>
    /// <param name="fullyQualifiedTestClassName"><c>TestContext.FullyQualifiedTestClassName</c>.</param>
    /// <param name="testName"><c>TestContext.TestName</c> — the method name.</param>
    /// <param name="writeLine"><c>TestContext.WriteLine</c>, so specification output lands on the test.</param>
    public static void Open(
        Assembly testAssembly, string? fullyQualifiedTestClassName, string? testName, Action<string> writeLine)
    {
        var testClass = fullyQualifiedTestClassName is null ? null : testAssembly.GetType(fullyQualifiedTestClassName);
        if (testClass is null || testName is null || !optedIn(testClass, testName)) return;

        MarkerStepRun.BeginScenario(testClass, testName, Mode);
        _output.Value = SpecOutput.Open(writeLine);
    }

    /// <summary>
    /// Close the scenario <see cref="Open"/> opened, with the outcome MSTest recorded. Does nothing
    /// for a test that was not opted in.
    /// </summary>
    /// <param name="outcome"><c>TestContext.CurrentTestOutcome</c>, by name.</param>
    /// <param name="exception"><c>TestContext.TestException</c>.</param>
    /// <remarks>
    /// <b>Throwing here is how a gathered wrong reaches the runner</b>, as in the other adapters:
    /// MSTest fails a test whose cleanup throws, so a specification whose <c>SpecAssert</c> checks
    /// failed without throwing still turns the test red.
    /// </remarks>
    public static void Close(string outcome, Exception? exception)
    {
        if (ScenarioRecorder.Current is null) return;

        // Before the sink closes: EndScenario is what raises ScenarioCompleted, and that is where
        // the scenario's reports are written.
        var gathered = MarkerStepRun.EndScenario(VerdictFrom(outcome, exception));

        _output.Value?.Dispose();
        _output.Value = null;

        if (gathered is not null) throw gathered;
    }

    /// <summary>
    /// Translate MSTest's verdict into Bobcat's. Public because it is the only part of this adapter
    /// with a decision in it, and a decision worth testing directly.
    /// </summary>
    /// <param name="outcome">
    /// The name of MSTest's <c>UnitTestOutcome</c> value. A name rather than the enum because this
    /// assembly references no MSTest major (see the class remarks).
    /// </param>
    /// <param name="exception">What the test threw, if anything.</param>
    public static ScenarioVerdict VerdictFrom(string? outcome, Exception? exception)
        => outcome switch
        {
            "Passed" => ScenarioVerdict.Passed,

            // A timeout is a failure with a boring exception, not an absence of one.
            "Failed" or "Error" or "Timeout"
                => ScenarioVerdict.FromException(unwrap(exception)) is { Kind: ScenarioVerdictKind.Failed } failed
                    ? failed
                    : ScenarioVerdict.Failed(null, outcome),

            // Inconclusive, Ignored, Aborted, NotRunnable, NotFound, InProgress, Unknown, or nothing: the
            // body did not make its assertions, and every outcome word Bobcat has would claim it did.
            _ => ScenarioVerdict.NotClaimed
        };

    /// <summary>
    /// MSTest invokes a test through reflection, so the exception it records can be the invocation
    /// wrapper rather than what the test threw.
    /// </summary>
    private static Exception? unwrap(Exception? exception)
        => exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;

    private static bool optedIn(Type testClass, string testName)
        => testClass.IsDefined(typeof(BobcatScenarioAttribute), inherit: true)
           || testClass.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
               .Any(m => m.Name == testName && m.IsDefined(typeof(BobcatScenarioAttribute), inherit: true));
}
