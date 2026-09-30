using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// BOBCAT031: a set verification over a collection of plain values has no properties to read
/// columns from, so the fixture has to name the one column.
/// </summary>
public class SetOfValuesTests
{
    private static string fixture(string attribute, string returns) => $$"""
        using Bobcat;
        using System.Collections.Generic;

        namespace Probe;

        public record Detail(string Name);

        public class ProbeFixture : Fixture
        {
            [Then("the names should be")]
            {{attribute}}
            public {{returns}} TheNamesShouldBe() => null!;
        }
        """;

    private const string Feature = """
        Feature: Probe

          Scenario: names
            Then the names should be
              | Name |
              | Luke |
        """;

    private static GeneratorHarness.RunOutcome run(string attribute, string returns)
        => GeneratorHarness.Run(fixture(attribute, returns), ("Probe.feature", Feature));

    [Fact]
    public void a_set_of_strings_with_no_column_named_is_BOBCAT031()
    {
        var message = run("[SetVerification]", "IEnumerable<string>")
            .WithId("BOBCAT031").ShouldHaveSingleItem().GetMessage();

        message.ShouldContain("the names should be");
        message.ShouldContain("TheNamesShouldBe");
        message.ShouldContain("Column");
    }

    [Fact]
    public void a_set_of_strings_that_names_its_column_is_fine()
    {
        var outcome = run("[SetVerification(Column = \"Name\")]", "IEnumerable<string>");

        outcome.WithId("BOBCAT031").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();
        outcome.GeneratedSource("Probe_Feature").ShouldContain("scalarColumn: \"Name\"");
    }

    [Fact]
    public void an_array_of_values_is_the_same_case()
    {
        run("[SetVerification]", "int[]").WithId("BOBCAT031").ShouldHaveSingleItem();
    }

    [Fact]
    public void an_enum_is_a_value_too()
    {
        run("[SetVerification]", "IEnumerable<System.DayOfWeek>").WithId("BOBCAT031").ShouldHaveSingleItem();
    }

    [Fact]
    public void a_set_of_objects_needs_no_column_because_its_headers_name_properties()
    {
        var outcome = run("[SetVerification(KeyColumns = \"Name\")]", "IEnumerable<Detail>");

        outcome.WithId("BOBCAT031").ShouldBeEmpty();
        outcome.GeneratedSource("Probe_Feature").ShouldNotContain("scalarColumn");
    }

    [Fact]
    public void an_awaited_set_of_values_is_read_through_the_task()
    {
        run("[SetVerification]", "System.Threading.Tasks.Task<IEnumerable<string>>")
            .WithId("BOBCAT031").ShouldHaveSingleItem();
    }
}
