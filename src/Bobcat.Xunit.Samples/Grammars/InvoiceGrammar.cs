namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// Storyteller 5's <c>CurryingFixture</c> — how a wordy grammar is narrowed to the one thing a
/// specification actually cares about.
/// </summary>
/// <remarks>
/// <para>
/// Storyteller curried a grammar at declaration time, supplying defaults and a shorter sentence:
/// </para>
/// <code>
/// [FormatAs("Invoice {Id} is open {IsOpen} and due on {DueDate}")]
/// public void CreateInvoice(string Id, DateTime DueDate, bool IsOpen) { }
///
/// public IGrammar OpenInvoice() =>
///     this["CreateInvoice"].Curry()
///         .Template("Invoice {Id} is open")
///         .Defaults("DueDate:TODAY+2,IsOpen:true");
/// </code>
/// <para>
/// <b>C# curries two ways and neither needs a feature.</b> Optional parameters shorten the CALL
/// while leaving the sentence as written; a second <c>[BobcatStep]</c> delegating to the first
/// shortens the SENTENCE too, which is what Storyteller's <c>.Template(...)</c> was for. The
/// delegating form is the faithful one, and it costs three lines.
/// </para>
/// <para>
/// <b>What is genuinely lost.</b> Storyteller's defaults were written as spec-language text —
/// <c>TODAY+2</c> — parsed by the cell's converter. A C# default has to be a compile-time constant,
/// so a relative date becomes code in the delegating helper's body. Bobcat's Gherkin lane still
/// reads <c>TODAY+2</c> (see <c>RelativeTimeResolver</c>); the projected lane has no cell text to
/// read it from.
/// </para>
/// </remarks>
public class InvoiceGrammar
{
    private readonly List<string> _open = new();

    [Given("Invoice {id} is open {isOpen} and due on {dueDate}")]
    internal void CreateInvoice(string id, DateOnly dueDate, bool isOpen)
    {
        if (isOpen) _open.Add(id);
    }

    /// <summary>The curried form: the only fact the specification states is the invoice's id.</summary>
    [Given("Invoice {id} is open")]
    internal void OpenInvoice(string id)
        => CreateInvoice(id, DateOnly.FromDateTime(DateTime.Today).AddDays(2), isOpen: true);

    [Then("Invoice {id} should be open")]
    internal void InvoiceShouldBeOpen(string id)
        => SpecAssert.Fact(_open.Contains(id), $"The open invoices are [{string.Join(", ", _open)}]");

    [Then("{count} invoices should be open")]
    internal void OpenInvoiceCountShouldBe(int count) => SpecAssert.Check("count", _open.Count, count);
}
