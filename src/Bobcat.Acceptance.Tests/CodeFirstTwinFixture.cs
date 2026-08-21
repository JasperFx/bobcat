using Bobcat;
using Bobcat.CodeFirst;

namespace Bobcat.Acceptance.Tests;

public record LedgerEntry(string Kind, int Amount);

/// <summary>
/// The Gherkin half of the twin: <c>Features/CodeFirstTwin.feature</c> binds to these steps
/// through the generator.
/// </summary>
public class CodeFirstTwinFixture : Fixture
{
    private int _balance;
    private readonly List<LedgerEntry> _ledger = new();

    [Given("an account opened with {int}")]
    public void Open(int amount)
    {
        _balance = amount;
        _ledger.Add(new LedgerEntry("Opened", amount));
    }

    [When("{int} is deposited")]
    public void Deposit(int amount)
    {
        _balance += amount;
        _ledger.Add(new LedgerEntry("Deposit", amount));
    }

    [Then("the balance should be {int}")]
    public int Balance() => _balance;

    [Then("the ledger should be")]
    [SetVerification(KeyColumns = "Kind")]
    public IEnumerable<LedgerEntry> Ledger() => _ledger;
}

/// <summary>
/// The code-first half: the same scenario declared in C#, no feature file, no generator. The
/// acceptance test for issue #105 renders both and asserts the <c>SpecRender</c> shapes agree.
/// </summary>
[FixtureTitle("Code First Twin")]
public class CodeFirstTwinSpecification : Specification
{
    private int _balance;
    private readonly List<LedgerEntry> _ledger = new();

    [Scenario("Depositing into an account")]
    public void depositing_into_an_account()
    {
        Given("an account opened with 100", () => open(100));
        When("25 is deposited", () => deposit(25));
        Then("the balance", () => _balance).ShouldBe(125);
        ThenRows("the ledger", () => _ledger).KeyedBy("Kind")
            .ShouldMatch(new { Kind = "Opened", Amount = 100 }, new { Kind = "Deposit", Amount = 25 });
    }

    private void open(int amount)
    {
        _balance = amount;
        _ledger.Add(new LedgerEntry("Opened", amount));
    }

    private void deposit(int amount)
    {
        _balance += amount;
        _ledger.Add(new LedgerEntry("Deposit", amount));
    }
}
