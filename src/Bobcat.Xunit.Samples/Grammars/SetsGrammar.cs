using Bobcat.Runtime;

namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// Storyteller's <c>InvoiceDetail</c>. <c>Date</c> is a real <see cref="DateOnly"/> — the samples'
/// own data says <c>TODAY</c> and <c>TODAY-1</c>, read as a date on the way in and asserted as one
/// on the way out.
/// </summary>
public record InvoiceDetail(double Amount, DateOnly Date, string Name);

/// <summary>Storyteller's <c>DataTableFixture</c> row — a city read back "from the database".</summary>
public record CityRow(string City, int Distance, string Zip);

/// <summary>
/// Storyteller's <c>SetsFixture</c>, <c>DataTableFixture</c> and <c>NameListFixture</c> samples from
/// the C# side: a set verification the grammar makes itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>The expected rows are an argument.</b> A step taking a <see cref="StepTable"/> receives the
/// expected rows from the test, and <see cref="SetVerificationComparer.Verify"/> compares them
/// against the actual collection, with key columns, ordering and column selection as arguments.
/// </para>
/// <para>
/// A grammar is a plain class, so it reaches the engine through public statics, as
/// <see cref="RosterGrammar"/> calls <c>TableRunner.BuildRows</c>.
/// </para>
/// </remarks>
public class SetsGrammar
{
    private readonly List<InvoiceDetail> _details = new();
    private readonly List<CityRow> _cities = new();
    private readonly List<string> _names = new();

    /// <summary>
    /// Storyteller's <c>InvoiceDetailsAre</c>, whose only job is to set up the actual state the
    /// verification then reads. <c>TODAY-1</c> is a date because Bobcat's cell conversion
    /// (<c>CellValues</c>) reads it as one.
    /// </summary>
    [Given("the invoice details are")]
    internal void TheInvoiceDetailsAre(StepTable table)
    {
        _details.Clear();
        _details.AddRange(TableRunner.BuildRows<InvoiceDetail>(table));
    }

    /// <summary>
    /// Storyteller's <c>UnorderedDetailsAre</c>: <c>VerifySetOf(() =&gt; _details).MatchOn(...)</c>.
    /// <c>keyColumns</c> names the columns that identify a row; every other column the table writes
    /// is compared once the row is matched.
    /// </summary>
    [Then("the unordered details should be")]
    internal void TheUnorderedDetailsShouldBe(StepTable expected)
        => SetVerificationComparer.Verify(_details, expected, keyColumns: "Name");

    /// <summary>
    /// Storyteller's <c>OrderedDetailsAre</c>: the same grammar with one word changed, because
    /// whether order is part of the claim is a property of the assertion rather than a different
    /// kind of assertion.
    /// </summary>
    [Then("the ordered details should be")]
    internal void TheOrderedDetailsShouldBe(StepTable expected)
        => SetVerificationComparer.Verify(_details, expected, keyColumns: "Name", ordered: true);

    /// <summary>Storyteller's <c>DataTableFixture.TheDataTableIs</c>.</summary>
    [Given("the cities in the database are")]
    internal void TheCitiesAre(StepTable table)
    {
        _cities.Clear();
        _cities.AddRange(TableRunner.BuildRows<CityRow>(table));
    }

    /// <summary>
    /// Storyteller's <c>VerifyRows</c> — the grammar the Data_Tables specification calls four times
    /// to show a happy path, an extra row, a missing row and a mismatch one after another.
    /// </summary>
    [Then("the rows in the database should be")]
    internal void TheRowsShouldBe(StepTable expected)
        => SetVerificationComparer.Verify(_cities, expected, keyColumns: "City");

    /// <summary>Storyteller's <c>NameListFixture.TheNamesAre</c>.</summary>
    [Given("the names are")]
    internal void TheNamesAre(StepTable table)
    {
        _names.Clear();
        _names.AddRange(table.Rows.Select(r => r[0]));
    }

    /// <summary>
    /// Storyteller's <c>VerifyStringList(() =&gt; _names).Titled("…", "Name").Ordered()</c>. A set of
    /// plain values needs no wrapper record and no column named: the table has one column, so there
    /// is only one thing it could be.
    /// </summary>
    [Then("the names in order should be")]
    internal void TheNamesShouldBe(StepTable expected)
        => SetVerificationComparer.Verify(_names, expected, ordered: true);
}
