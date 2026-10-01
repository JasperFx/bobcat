using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// What <c>emitSetVerificationStep</c> writes. Both facts here are invisible to a behaviour test —
/// one because a columnless grid still renders, the other because two readings of a key list produce
/// the same array until one of them drifts — so the emitted source is what has to be pinned.
/// </summary>
public class SetVerificationEmissionTests
{
    private const string Fixture = """
        using Bobcat;
        using System.Collections.Generic;

        namespace Probe;

        public record Detail(string Name, decimal Amount);

        public class ProbeFixture : Fixture
        {
            [Then("the details should be")]
            [SetVerification(KeyColumns = "Name, Amount")]
            public IEnumerable<Detail> TheDetailsShouldBe() => null!;

            [Then("the details should be anything")]
            [SetVerification]
            public IEnumerable<Detail> TheDetailsShouldBeAnything() => null!;
        }
        """;

    private static GeneratorHarness.RunOutcome run(string feature)
        => GeneratorHarness.Run(Fixture, ("Probe.feature", feature));

    private static string sourceFor(string steps)
    {
        var outcome = run($"""
            Feature: Probe

              Scenario: details
            {steps}
            """);

        outcome.CompilationErrors.ShouldBeEmpty();
        return outcome.GeneratedSource("Probe_Feature");
    }

    [Fact]
    public void the_header_row_is_emitted_beside_the_rows()
    {
        sourceFor("""
                Then the details should be
                  | Name | Amount |
                  | Cord | 100    |
            """)
            .ShouldContain("""columns: new[] { "Name", "Amount" }""");
    }

    [Fact]
    public void a_table_with_a_header_and_no_rows_still_names_its_columns()
    {
        // The case the headers exist for. Read off the first expected row there is no first row, so
        // the grid came out with no columns at all — and "the set should be empty" is a real
        // expectation whose extra rows then rendered under no headings.
        sourceFor("""
                Then the details should be
                  | Name | Amount |
            """)
            .ShouldContain("""columns: new[] { "Name", "Amount" }""");
    }

    [Fact]
    public void the_columns_are_emitted_in_the_order_the_document_wrote_them()
    {
        sourceFor("""
                Then the details should be
                  | Amount | Name |
                  | 100    | Cord |
            """)
            .ShouldContain("""columns: new[] { "Amount", "Name" }""");
    }

    [Fact]
    public void the_key_list_is_read_by_the_runtime_parser_rather_than_split_here()
    {
        // One authority on what "Name, Amount" means, so a key list cannot mean one thing in an
        // attribute and another as a VerifySet argument. The generated code runs in an assembly that
        // references Bobcat, so there is no reason for a second reading to exist.
        var source = sourceFor("""
                Then the details should be
                  | Name | Amount |
                  | Cord | 100    |
            """);

        source.ShouldContain(
            """global::Bobcat.Runtime.SetVerificationComparer.ParseKeyColumns("Name, Amount")""");
    }

    [Fact]
    public void no_key_columns_at_all_is_an_empty_array_and_not_a_parse()
    {
        var source = sourceFor("""
                Then the details should be anything
                  | Name | Amount |
                  | Cord | 100    |
            """);

        source.ShouldContain("global::System.Array.Empty<string>()");
        source.ShouldNotContain("ParseKeyColumns");
    }
}
