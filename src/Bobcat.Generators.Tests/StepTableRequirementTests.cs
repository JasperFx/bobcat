using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// BOBCAT020, issue #233: a step used without a trailing data table, bound to a method that
/// declares a non-nullable <c>StepTable</c>, is a build error rather than a null-deref at run time.
/// </summary>
/// <remarks>
/// What it replaces is what a scaffolded scenario actually produced:
/// <code>
/// System.NullReferenceException: Object reference not set to an instance of an object.
///    at Bobcat.CritterStack.CritterStackFixture.WhenCommandIsReceived(Type command, StepTable fields)
/// </code>
/// The Gherkin was legal and the step bound fine; the table was simply absent, the generator
/// passed null, and the fixture dereferenced it — sending the reader into Bobcat's own stack
/// instead of naming their step. The distinction the fix rests on was already in the type: the
/// binding's own documentation said "null when the step has none <em>and the parameter is
/// nullable</em>". Nothing enforced the second half.
/// </remarks>
public class StepTableRequirementTests
{
    private const string Fixture =
        """
        using System.Threading.Tasks;
        using Bobcat;

        namespace Specs;

        [FixtureTitle("Ledger")]
        public class LedgerFixture : Fixture
        {
            [Given("the ledger holds")]
            public void GivenTheLedgerHolds(StepTable rows) { }

            [When("{string} is posted")]
            public void WhenPosted(string reference, StepTable? fields) { }
        }
        """;

    private static string feature(string body) =>
        $"""
         Feature: Ledger

           Scenario: A posting
         {body}
         """;

    [Fact]
    public void a_step_without_a_table_whose_method_requires_one_is_an_error()
    {
        var outcome = GeneratorHarness.Run(Fixture,
            ("Ledger.feature", feature("    Given the ledger holds")));

        var diagnostic = outcome.WithId("BOBCAT020").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("the ledger holds");
        diagnostic.GetMessage().ShouldContain("GivenTheLedgerHolds");
        diagnostic.GetMessage().ShouldContain("declare the parameter as 'StepTable?'");
        diagnostic.Severity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void the_same_step_with_a_table_is_fine()
    {
        var outcome = GeneratorHarness.Run(Fixture,
            ("Ledger.feature", feature("""
                                           Given the ledger holds
                                             | Account | Amount |
                                             | cash    | 10     |
                                       """)));

        outcome.WithId("BOBCAT020").ShouldBeEmpty();
    }

    [Fact]
    public void a_nullable_table_parameter_says_the_step_works_without_one()
    {
        // The other half of the fix: `StepTable?` is the author saying the table is optional, and
        // the generator has to take them at their word — CritterStackFixture's bus act is exactly
        // this case, since a field-less command is a perfectly good act.
        var outcome = GeneratorHarness.Run(Fixture,
            ("Ledger.feature", feature("    When \"REF-1\" is posted")));

        outcome.WithId("BOBCAT020").ShouldBeEmpty();
    }
}
