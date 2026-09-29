namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// Storyteller 5's action grammars — <c>ExplicitExecution</c> and the implicit-action
/// <c>BatchProcessFixture</c>, whose work happened off stage in <c>SetUp()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The explicit half maps one for one</b>: a <c>When</c> step that kicks the process off and
/// waits. It is also the grammar whose duration is worth reading, so it is the one place these
/// samples deliberately take measurable time.
/// </para>
/// <para>
/// <b>The implicit half is where the two models genuinely differ.</b> Storyteller's <c>SetUp()</c>
/// ran before the first step and any exception from it was surfaced into the results HTML, so an
/// off-stage failure still landed on the specification. A projected test's setup is a constructor or
/// an <c>IAsyncLifetime</c>, and neither is a step: work done there appears in the report only if a
/// helper reports it. <see cref="TheBatchProcessHasRun"/> is that helper — the honest way to keep
/// off-stage work visible is to say so in one step.
/// </para>
/// </remarks>
public class BatchProcessGrammar
{
    private bool _hasRun;
    private int _recordsProcessed;

    /// <summary>Storyteller: <c>[FormatAs("Start the batch process")]</c>, the explicit action.</summary>
    [When("Start the batch process and wait for it to finish")]
    internal async Task StartTheBatchProcess()
    {
        // Real work, so the rendered duration is a real measurement rather than a rounding artifact.
        await Task.Delay(35);

        _hasRun = true;
        _recordsProcessed = 12;
    }

    /// <summary>
    /// The implicit action, made visible. Called from the test body after a constructor or
    /// <c>IAsyncLifetime</c> has already done the work, purely so the specification says it happened.
    /// </summary>
    [Given("The batch process has already run")]
    internal void TheBatchProcessHasRun()
        => SpecAssert.Fact(_hasRun, "The batch process has not been started");

    [Then("{count} records should have been processed")]
    internal void RecordsProcessedShouldBe(int count)
        => SpecAssert.Check("count", _recordsProcessed, count);

    /// <summary>
    /// Storyteller's <c>LoggingFixture</c> — <c>Context.Reporting.Log(...)</c> attaches custom
    /// output to the step, and the report shows it under the sentence.
    /// </summary>
    /// <remarks>
    /// <b>There is no projected equivalent yet.</b> A recorded step carries cells and a failure, but
    /// no logs and no diagnostics, although <c>StepRender</c> renders both for the Gherkin lane. This
    /// grammar exists to name the gap rather than to demonstrate a feature: the message goes to the
    /// test output, where the specification cannot see it.
    /// </remarks>
    [When("Do something that requires custom logging")]
    internal void DoSomethingWorthLogging()
        => TestContext.Current.TestOutputHelper?.WriteLine("I am making a custom log");
}
