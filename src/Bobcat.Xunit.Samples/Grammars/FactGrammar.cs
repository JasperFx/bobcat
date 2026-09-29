namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// Storyteller 5's <c>FactFixture</c> — the Fact grammar, which asserts one boolean condition and
/// renders as a single line going green or red.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storyteller's shape, unchanged.</b> A Fact is a <c>bool</c>-returning method whose return value
/// does NOT appear in the sentence, and the answer IS the verdict:
/// </para>
/// <code>
/// [Then("This line is always false")]
/// internal bool ThisLineIsAlwaysFalse() => false;
/// </code>
/// <para>
/// No <c>[Check]</c> needed and nothing to report by hand. <c>Task&lt;bool&gt;</c> works the same way
/// and carries a real duration — the shape Storyteller had no equivalent of, because it was written
/// before async/await was common.
/// </para>
/// <para>
/// <see cref="SpecAssert.Fact(bool, string)"/> is still there for the case worth reaching for: a fact
/// with a <i>reason</i> for its failure. A bare fact reports none, because the red line already says
/// "not true" and a manufactured sentence is the noise Storyteller's <c>StoryTellerAssert</c> existed
/// to replace.
/// </para>
/// <para>
/// <b>Why a fact is a wrong and not an error.</b> A false fact is the system disagreeing with the
/// specification; an exception is the system breaking. Storyteller counted them separately
/// (<i>wrongs</i> against <i>exceptions</i>) and so does Bobcat — a failed fact is
/// <c>ResultStatus.failed</c> and lets the scenario continue, while a thrown exception is
/// <c>ResultStatus.error</c> and stops it.
/// </para>
/// </remarks>
public class FactGrammar
{
    /// <summary>
    /// Storyteller's programmatic fact — <c>Fact("The thing is activated").VerifiedBy(() => true)</c>,
    /// declared in the fixture constructor. A projected test has no constructor-time grammar
    /// building: a helper IS the declaration.
    /// </summary>
    [Then("The thing is activated")]
    internal bool TheThingIsActivated() => true;

    [Then("This line is always true")]
    internal bool ThisLineIsAlwaysTrue() => true;

    [Then("This line is always false")]
    internal bool ThisLineIsAlwaysFalse() => false;

    /// <summary>
    /// The shape Storyteller never had: an <b>asynchronous</b> fact. It carries a real duration, and
    /// the step ends when the work does rather than when the Task is handed back.
    /// </summary>
    [Then("The ledger balances")]
    internal async Task<bool> TheLedgerBalances()
    {
        await Task.Delay(15);
        return false;
    }

    /// <summary>
    /// Storyteller's <c>ThisLineAlwaysThrowsExceptions</c> — <c>throw new
    /// DivideByZeroException("You can't do this!")</c>. An error, and it ends the test.
    /// </summary>
    [Then("This line always throws exceptions")]
    internal void ThisLineAlwaysThrowsExceptions()
        => throw new DivideByZeroException("You can't do this!");

    /// <summary>
    /// Storyteller's <c>TheConfirmationEmailWasSent</c>, the sample for "providing more context on
    /// failures": <c>StoryTellerAssert.Fail(true, "The email server is not reachable")</c> — a
    /// failure with a readable reason and <b>no stack trace</b>.
    /// </summary>
    /// <remarks>
    /// <see cref="SpecAssert.Fail(bool, string)"/> keeps Storyteller's polarity — fail WHEN the
    /// condition holds — so a suite being ported reads the same. It records a
    /// <c>SpecAssertionException</c> that is never thrown, which is what gets the message into the
    /// report without a stack and without ending the scenario.
    /// </remarks>
    [Then("The confirmation email was sent")]
    internal void TheConfirmationEmailWasSent()
        => SpecAssert.Fail(true, "The email server is not reachable");

    /// <summary>
    /// The same failure written with a reason on the fact itself, which is the shape worth
    /// recommending: one call, and the message is attached to the claim it explains.
    /// </summary>
    [Then("The invoice {id} has been settled")]
    internal void TheInvoiceHasBeenSettled(string id)
        => SpecAssert.Fact(false, $"Invoice {id} is still open, with 42.00 outstanding");
}
