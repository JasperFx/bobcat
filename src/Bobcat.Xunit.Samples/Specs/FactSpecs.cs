using Bobcat.Xunit.Samples.Grammars;
using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// Storyteller 5's <c>Specs/Facts</c> suite recreated — the Fact grammar in every outcome it has.
/// </summary>
[BobcatFeature("Facts"), BobcatScenario]
public class FactSpecs
{
    private readonly FactGrammar _facts = new();

    /// <summary>
    /// Storyteller: <c>Specs/Facts/Facts_in_Action.md</c>, which runs all five fact grammars in one
    /// specification — two true, one false, one throwing, one failing with a message.
    /// </summary>
    /// <remarks>
    /// <b>This one cannot be recreated faithfully, and that is the finding.</b> Storyteller reached
    /// all five lines: the false fact was a wrong and the run carried on, the exception was an
    /// error and the run carried on, and the report showed five verdicts. An xUnit test stops at the
    /// thrown exception, so the two steps after it never run. See
    /// <see cref="facts_in_action_without_the_throwing_line"/> for the part that DOES survive, and
    /// the README for what closing the gap would take.
    /// </remarks>
    [Fact]
    public void facts_in_action()
    {
        _facts.TheThingIsActivated();
        _facts.ThisLineIsAlwaysTrue();
        _facts.ThisLineIsAlwaysFalse();
        _facts.ThisLineAlwaysThrowsExceptions();
        _facts.TheConfirmationEmailWasSent();
    }

    /// <summary>
    /// The same specification with the throwing line removed: four facts, two of them wrong, and
    /// every one of the four reaches the report. This is the shape a projected suite can rely on.
    /// </summary>
    [Fact]
    public void facts_in_action_without_the_throwing_line()
    {
        _facts.TheThingIsActivated();
        _facts.ThisLineIsAlwaysTrue();
        _facts.ThisLineIsAlwaysFalse();
        _facts.TheConfirmationEmailWasSent();
        _facts.TheInvoiceHasBeenSettled("INV-1001");
    }

    /// <summary>Every fact true — the all-green rendering, for contrast.</summary>
    [Fact]
    public void every_fact_holds()
    {
        _facts.TheThingIsActivated();
        _facts.ThisLineIsAlwaysTrue();
    }

    /// <summary>
    /// The facts as Storyteller actually wrote them: <c>bool</c>-returning methods whose answer is the
    /// verdict, with nothing reported by hand. Plus a <c>Task&lt;bool&gt;</c>, which Storyteller had no
    /// equivalent of — note it carries a real duration.
    /// </summary>
    [Fact]
    public async Task facts_answer_with_their_return_value()
    {
        // The answer flows through to the caller untouched, so a test can still branch on it.
        _facts.ThisLineIsAlwaysTrue().ShouldBeTrue();

        // And a false one fails its step without ending the test, so the async fact below still runs.
        //
        // This comment opens with "And" and is STILL only a comment: no step has opened a narrative in
        // this test, and And/But continue a narrative rather than starting one. Before that rule it
        // became a step and wrapped the two real steps under a narrative row nobody wrote.
        _facts.ThisLineIsAlwaysFalse();

        await _facts.TheLedgerBalances();
    }
}
