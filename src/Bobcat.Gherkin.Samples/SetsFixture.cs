using Bobcat;

namespace Bobcat.Gherkin.Samples;

/// <summary>Storyteller's <c>InvoiceDetail</c>, as a record.</summary>
public record InvoiceDetail(double Amount, string Date, string Name);

/// <summary>Storyteller's <c>DataTableFixture</c> row — a city read back "from the database".</summary>
public record CityRow(string City, int Distance, string Zip);

public enum Colour
{
    Blue,
    Red,
    Orange,
    Green
}

/// <summary>
/// Storyteller's <c>SetsFixture</c> and <c>DataTableFixture</c> samples, recreated. The grammar is
/// <see cref="SetVerificationAttribute"/>: a <c>[Then]</c> returning a collection, compared against
/// the step's table by key columns, rendered as a grid with a verdict per row and per cell.
/// </summary>
public class SetsFixture : Fixture
{
    private readonly List<InvoiceDetail> _details = new();
    private readonly List<CityRow> _cities = new();
    private readonly List<Colour> _colours = new();
    private bool _fetchThrows;

    public void BeforeEach()
    {
        _details.Clear();
        _cities.Clear();
        _colours.Clear();
        _fetchThrows = false;
    }

    /// <summary>
    /// Storyteller's <c>InvoiceDetailsAre</c> — <c>CreateNewObject&lt;InvoiceDetail&gt;(...).AsTable(...)
    /// .Before(() =&gt; _details.Clear())</c>, whose only job is to set up the actual state the
    /// verification then reads.
    /// </summary>
    [Given("the invoice details are")]
    [Table]
    public void TheInvoiceDetailsAre(double amount, string date, string name)
        => _details.Add(new InvoiceDetail(amount, date, name));

    /// <summary>
    /// Storyteller's <c>UnorderedDetailsAre</c>: <c>VerifySetOf(() =&gt; _details)
    /// .MatchOn(o =&gt; o.Amount, o =&gt; o.Date, o =&gt; o.Name)</c>. Bobcat's <c>KeyColumns</c> is
    /// the same idea named differently — the columns that identify a row, with every other column
    /// compared once the row is matched.
    /// </summary>
    [Then("the unordered details should be")]
    [SetVerification(KeyColumns = "Name")]
    public IEnumerable<InvoiceDetail> TheUnorderedDetailsShouldBe() => _details;

    /// <summary>Storyteller's <c>DataTableFixture.TheDataTableIs</c>.</summary>
    [Given("the cities in the database are")]
    [Table]
    public void TheCitiesAre(string city, int distance, string zip)
        => _cities.Add(new CityRow(city, distance, zip));

    /// <summary>
    /// Storyteller's <c>VerifyRows</c> — the specification that shows a happy path, extra rows,
    /// missing rows and a mismatched row one after another.
    /// </summary>
    [Then("the rows in the database should be")]
    [SetVerification(KeyColumns = "City")]
    public IEnumerable<CityRow> TheRowsShouldBe()
    {
        if (_fetchThrows) throw new InvalidOperationException("the query could not be run");
        return _cities;
    }

    /// <summary>Storyteller's <c>ThrowsErrorOnDataFetch</c>.</summary>
    [Given("the query is broken")]
    public void TheQueryIsBroken() => _fetchThrows = true;

    /// <summary>Storyteller's <c>SetWithEnum.TheColorsAre</c>.</summary>
    [Given("the colours are")]
    [Table]
    public void TheColoursAre(Colour colour) => _colours.Add(colour);

    /// <summary>
    /// Storyteller's <c>TheColorsShouldBe</c> — a set whose only column is not a primitive. The
    /// comparison reads the actual value's runtime type, so an enum needs no special grammar;
    /// the set is projected through a record because a set of bare values has no columns to name.
    /// See the README.
    /// </summary>
    [Then("the colours should be")]
    [SetVerification(KeyColumns = "Colour")]
    public IEnumerable<ColourRow> TheColoursShouldBe() => _colours.Select(c => new ColourRow(c));

    public record ColourRow(Colour Colour);
}
