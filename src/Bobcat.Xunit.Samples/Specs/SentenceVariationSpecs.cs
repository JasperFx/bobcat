using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// Storyteller 5's <c>Specs/Actions</c> suite — an action stated explicitly, and one that happened
/// off stage.
/// </summary>
[BobcatFeature("Actions"), BobcatScenario]
public class ActionSpecs
{
    private readonly BatchProcessGrammar _batch = new();

    /// <summary>Storyteller: <c>Specs/Actions/Explicit_Action.md</c>.</summary>
    [Fact]
    public async Task explicit_action()
    {
        await _batch.StartTheBatchProcess();
        _batch.RecordsProcessedShouldBe(12);
    }

    /// <summary>
    /// Storyteller: <c>Specs/Actions/Implicit_Action.md</c> — a specification with no action step at
    /// all, because the batch process was kicked off in the fixture's <c>SetUp()</c>. The projected
    /// version says so in a <c>Given</c>, which is the only way a reader of the report can know.
    /// </summary>
    [Fact]
    public async Task implicit_action()
    {
        await setUpOffStage();

        _batch.TheBatchProcessHasRun();
        _batch.RecordsProcessedShouldBe(12);
    }

    /// <summary>
    /// The off-stage failure Storyteller surfaced into the report from <c>SetUp()</c>. Here it is a
    /// <c>Given</c> that fails, because a projected test's real setup — a constructor, an
    /// <c>IAsyncLifetime</c> — is not a step and cannot report one.
    /// </summary>
    [Fact]
    public void off_stage_setup_never_ran()
    {
        _batch.TheBatchProcessHasRun();
        _batch.RecordsProcessedShouldBe(12);
    }

    /// <summary>
    /// Storyteller: <c>LoggingFixture</c> / <c>Specs/General</c> instrumentation. The log line does
    /// not reach the specification — see <see cref="BatchProcessGrammar.DoSomethingWorthLogging"/>.
    /// </summary>
    [Fact]
    public async Task custom_logging_from_a_step()
    {
        _batch.DoSomethingWorthLogging();
        await _batch.StartTheBatchProcess();
        _batch.RecordsProcessedShouldBe(12);
    }

    private Task setUpOffStage() => _batch.StartTheBatchProcess();
}

/// <summary>Storyteller 5's <c>Specs/Currying/Currying.md</c>.</summary>
[BobcatFeature("Currying"), BobcatScenario]
public class CurryingSpecs
{
    private readonly InvoiceGrammar _invoices = new();

    /// <summary>
    /// The original, verbatim: one invoice stated in full, one stated through the curried grammar
    /// because "all the spec cares about is that the invoice is open".
    /// </summary>
    [Fact]
    public void currying()
    {
        _invoices.CreateInvoice("INV-1", DateOnly.FromDateTime(DateTime.Today).AddDays(5), isOpen: true);
        _invoices.OpenInvoice("INV-2");

        _invoices.InvoiceShouldBeOpen("INV-1");
        _invoices.InvoiceShouldBeOpen("INV-2");
        _invoices.OpenInvoiceCountShouldBe(2);
    }

    /// <summary>The same specification with a wrong count, so the cell has something to say.</summary>
    [Fact]
    public void currying_with_a_wrong_count()
    {
        _invoices.OpenInvoice("INV-2");
        _invoices.InvoiceShouldBeOpen("INV-3");
        _invoices.OpenInvoiceCountShouldBe(2);
    }
}

/// <summary>Storyteller 5's <c>AsyncOperationsFixture</c> samples.</summary>
[BobcatFeature("Asynchronous operations"), BobcatScenario]
public class AsyncSpecs
{
    private readonly AsyncGrammar _async = new();

    [Fact]
    public async Task asynchronous_grammars()
    {
        await _async.PerformTask();
        await _async.CheckName("Jeremy");
        await _async.IsConditionTrue();
    }

    [Fact]
    public async Task an_asynchronous_value_check_that_disagrees()
    {
        await _async.PerformTask();
        await _async.CheckName("Jerry");
        await _async.IsConditionTrue();
    }

    [Fact]
    public async Task an_asynchronous_step_that_throws()
    {
        await _async.PerformTask();
        await _async.RenameFails("Jerry");
        await _async.CheckName("Jerry");
    }
}
