using Bobcat;

namespace Bobcat.Acceptance.Tests;

public record Detail(string Name, decimal Amount);

/// <summary>
/// Storyteller's <c>_.Compare(o =&gt; o.Amount).Header("The Amount")</c>: a set's columns are its
/// result type's properties, so the title goes on the property. On a record that means targeting the
/// property explicitly, since the parameter and the property share a declaration.
/// </summary>
public record Ledger(
    [property: Header("Line Item")] string Name,
    [property: Header("The Amount")] decimal Amount);

public enum Grade
{
    Gold,
    Silver,
    Bronze
}

/// <summary>
/// The set-verification vocabulary beyond the unordered default: ordered comparison, a set of
/// plain values, and the two column options — a header of its own and an optional column.
/// </summary>
public class SetVerificationFixture : Fixture
{
    private readonly List<Detail> _details = new();
    private readonly List<string> _names = new();

    public void BeforeEach()
    {
        _details.Clear();
        _names.Clear();
        _ledger.Clear();
        Roster.Clear();
    }

    [Given("the details are")]
    [Table]
    public void TheDetailsAre(string name, decimal amount) => _details.Add(new Detail(name, amount));

    [Then("the details should be")]
    [SetVerification(KeyColumns = "Name")]
    public IEnumerable<Detail> TheDetailsShouldBe() => _details;

    [Then("the details in order should be")]
    [SetVerification(KeyColumns = "Name", Ordered = true)]
    public IEnumerable<Detail> TheDetailsInOrderShouldBe() => _details;

    [Given("the names are")]
    [Table]
    public void TheNamesAre(string name) => _names.Add(name);

    /// <summary>A set of plain values: <c>Column</c> names the one column they are compared under.</summary>
    [Then("the names in order should be")]
    [SetVerification(Column = "Name", Ordered = true)]
    public IEnumerable<string> TheNamesShouldBe() => _names;

    private readonly List<Ledger> _ledger = new();

    [Given("the ledger is")]
    [Table]
    public void TheLedgerIs(string name, decimal amount) => _ledger.Add(new Ledger(name, amount));

    /// <summary>
    /// The document names both columns by their titles. <c>KeyColumns</c> does too, because it names
    /// columns as the document writes them.
    /// </summary>
    [Then("the ledger should be")]
    [SetVerification(KeyColumns = "Line Item")]
    public IEnumerable<Ledger> TheLedgerShouldBe() => _ledger;

    internal readonly List<string> Roster = new();

    /// <summary>
    /// A column titled for the document rather than named after the parameter, and an optional
    /// column whose default the language supplies when the table leaves it out.
    /// </summary>
    [Given("the roster is")]
    [Table]
    public void TheRosterIs([Header("Player Name")] string player, Grade grade = Grade.Bronze)
        => Roster.Add($"{player}:{grade}");

    [Then("the roster reads {string}")]
    public string TheRosterReads() => string.Join("|", Roster);
}
