using Bobcat.Runtime;

namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>Storyteller's <c>Address</c> — six fields, of which a check names what it means.</summary>
public record Address(
    string Address1 = "",
    string Address2 = "",
    string City = "",
    string StateOrProvince = "",
    string Country = "",
    string PostalCode = "");

/// <summary>
/// Storyteller's <c>VerifyObject</c> / <c>CheckPropertyGrammar</c> from the C# side (issue #395).
/// </summary>
/// <remarks>
/// <para>
/// <b>One grammar body, both lanes</b>, exactly as for tables and sets:
/// <c>Bobcat.Gherkin.Samples/ObjectsFixture.cs</c> recreates these same documents declaratively, and
/// the grids are identical because the comparison is — <see cref="PropertyCells"/> underneath, and
/// <c>CellCheck</c> under that, so a property column and a set column disagree in the same words.
/// </para>
/// <para>
/// The static form is used because this grammar is a plain class rather than a <c>Fixture</c>, the
/// same reason <c>SetsGrammar</c> calls <c>SetVerificationComparer.Verify</c> and
/// <c>RosterGrammar</c> calls <c>TableRunner.BuildRows</c>. On a <c>Fixture</c> it is
/// <c>VerifyObject(...)</c> directly.
/// </para>
/// </remarks>
public class ObjectsGrammar
{
    private Address _address = new();

    [Given("the address is")]
    internal void TheAddressIs(StepTable table) => _address = TableRunner.BuildRows<Address>(table).Single();

    [Then("the address should be")]
    internal void TheAddressShouldBe(StepTable expected) => PropertyCells.Verify(_address, expected);
}
