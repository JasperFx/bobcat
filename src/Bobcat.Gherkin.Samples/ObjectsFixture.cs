using Bobcat;

namespace Bobcat.Gherkin.Samples;

/// <summary>
/// Storyteller's <c>Address</c> (Samples/Specs/Create Objects). Six fields, of which a document
/// names however many it means something by.
/// </summary>
public record Address(
    string Address1 = "",
    string Address2 = "",
    string City = "",
    string StateOrProvince = "",
    string Country = "",
    string PostalCode = "");

/// <summary>
/// Storyteller's <c>VerifyObject</c> / <c>CheckPropertyGrammar</c> samples, recreated —
/// Samples/Specs/Create Objects/Using_VerifyObject.md and
/// StoryTeller.Samples/Specs/General/Check properties.md.
/// </summary>
/// <remarks>
/// <para>
/// The grammar is <c>VerifyObject</c>: one object, one row, and the columns the row names compared
/// against the properties of those names. <b>Only those</b> — <c>Using_VerifyObject.md</c> names
/// three of the Address's six fields and means nothing by the other three, which is the partial rule
/// every other comparison here follows rather than a convention of its own.
/// </para>
/// <para>
/// There is no declarative twin, and that is the one thing worth saying out loud about this family:
/// <c>[SetVerification]</c> is canonical over <c>VerifySet</c> because its <c>KeyColumns</c> /
/// <c>Ordered</c> / <c>Column</c> are compile-time facts a tool can read, and here there is nothing
/// to configure — the columns come from the table and the subject from the method. So the argument
/// form is the only form.
/// </para>
/// </remarks>
public class ObjectsFixture : Fixture
{
    private Address _address = new();

    public void BeforeEach() => _address = new Address();

    /// <summary>Storyteller's <c>TheAddressIs</c>, as the arrange a check needs.</summary>
    [Given("the address is")]
    public void TheAddressIs(StepTable table) => _address = BuildRows<Address>(table).Single();

    /// <summary>
    /// Storyteller's <c>TheAddressShouldBe</c>. A cell may say <c>EMPTY</c>, exactly as
    /// <c>Using_VerifyObject.md</c> does for <c>Address2</c>, because the tokens read the same here
    /// as in every other cell.
    /// </summary>
    [Then("the address should be")]
    public void TheAddressShouldBe(StepTable expected) => VerifyObject(_address, expected);
}
