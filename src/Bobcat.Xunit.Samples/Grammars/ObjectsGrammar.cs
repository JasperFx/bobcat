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
/// The comparison is <see cref="PropertyCells"/> underneath, and <c>CellCheck</c> under that, so a
/// property column and a set column disagree in the same words.
/// </para>
/// <para>
/// A grammar is a plain class, so it reaches the engine through public statics:
/// <c>PropertyCells.Verify</c> here, as <c>SetsGrammar</c> calls
/// <c>SetVerificationComparer.Verify</c> and <c>RosterGrammar</c> calls <c>TableRunner.BuildRows</c>.
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
