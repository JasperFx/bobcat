using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// The types a written value can be read as, and what happens when it cannot. Every case here
/// broke the CONSUMER's build before BOBCAT030: the generator emitted the cell's text and the
/// compiler rejected it in a file the author cannot open.
/// </summary>
public class CellLiteralTests
{
    private const string Fixture = """
        using Bobcat;
        using System;

        namespace Probe;

        public enum Colour { Blue, Red, Orange }

        public class ProbeFixture : Fixture
        {
            [Given("an enum cell {word}")]
            public void EnumCell(Colour colour) { }

            [Given("a datetime cell {word}")]
            public void DateTimeCell(DateTime when) { }

            [Given("a timespan cell {word}")]
            public void TimeSpanCell(TimeSpan span) { }

            [Given("a dateonly cell {word}")]
            public void DateOnlyCell(DateOnly day) { }

            [Given("a timeonly cell {word}")]
            public void TimeOnlyCell(TimeOnly at) { }

            [Given("an offset cell {word}")]
            public void OffsetCell(DateTimeOffset when) { }

            [Given("a uri cell {word}")]
            public void UriCell(Uri where) { }

            [Given("a char cell {word}")]
            public void CharCell(char c) { }

            [Given("a nullable int cell {word}")]
            public void NullableIntCell(int? n) { }

            [Given("a nullable enum cell {string}")]
            public void NullableEnumCell(Colour? colour) { }

            [Given("a short cell {word}")]
            public void ShortCell(short s) { }

            [Given("an int cell {word}")]
            public void IntCell(int n) { }

            [Given("a bool cell {word}")]
            public void BoolCell(bool b) { }
        }
        """;

    private static GeneratorHarness.RunOutcome run(params string[] steps)
    {
        var feature = "Feature: Probe\n\n  Scenario: cells\n" +
                      string.Join("\n", steps.Select(s => "    Given " + s));
        return GeneratorHarness.Run(Fixture, ("Probe.feature", feature));
    }

    [Fact]
    public void every_type_the_binder_calls_a_value_is_a_type_the_generator_can_write()
    {
        var outcome = run(
            "an enum cell Blue",
            "a datetime cell 2026-01-01",
            "a timespan cell 00:05:00",
            "a dateonly cell 2026-01-01",
            "a timeonly cell 14:30",
            "an offset cell 2026-01-01",
            "a uri cell /orders/5",
            "a char cell x",
            "a nullable int cell 5",
            "a nullable enum cell \"\"",
            "a short cell 3",
            "an int cell 42",
            "a bool cell true");

        outcome.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void an_enum_cell_becomes_a_qualified_member_reference()
    {
        var source = run("an enum cell Blue").GeneratedSource("Probe_Feature");
        source.ShouldContain("f.EnumCell(global::Probe.Colour.Blue)");
    }

    [Fact]
    public void an_enum_cell_is_matched_without_regard_to_case()
    {
        var source = run("an enum cell orange").GeneratedSource("Probe_Feature");
        source.ShouldContain("f.EnumCell(global::Probe.Colour.Orange)");
    }

    [Fact]
    public void a_numeric_enum_cell_is_a_cast_because_flags_combinations_name_no_member()
    {
        var source = run("an enum cell 7").GeneratedSource("Probe_Feature");
        source.ShouldContain("f.EnumCell((global::Probe.Colour)(7))");
    }

    [Fact]
    public void an_empty_cell_against_a_nullable_value_type_is_null()
    {
        var source = run("a nullable enum cell \"\"").GeneratedSource("Probe_Feature");
        source.ShouldContain("f.NullableEnumCell(null)");
    }

    [Fact]
    public void a_date_is_parsed_with_the_invariant_culture_so_the_build_machine_does_not_decide()
    {
        var source = run("a datetime cell 2026-01-01").GeneratedSource("Probe_Feature");
        source.ShouldContain("global::System.DateTime.Parse(\"2026-01-01\", " +
                             "global::System.Globalization.CultureInfo.InvariantCulture)");
    }

    [Fact]
    public void a_value_that_cannot_be_read_is_BOBCAT030_naming_the_step_and_the_parameter()
    {
        var outcome = run("an int cell oops");

        var diagnostic = outcome.WithId("BOBCAT030").ShouldHaveSingleItem();
        var message = diagnostic.GetMessage();
        message.ShouldContain("an int cell oops");
        message.ShouldContain("'n'");
        message.ShouldContain("'oops' is not an int");
    }

    [Fact]
    public void an_enum_cell_naming_no_member_lists_the_members()
    {
        var message = run("an enum cell Purple").WithId("BOBCAT030").ShouldHaveSingleItem().GetMessage();
        message.ShouldContain("Blue, Red, Orange");
    }

    [Fact]
    public void an_unreadable_value_suppresses_the_feature_rather_than_emitting_code_that_will_not_compile()
    {
        var outcome = run("an int cell oops");

        // The whole point: before this the author's build failed with a CS error inside
        // Probe_Feature.g.cs, a file they cannot edit, over a step that matched.
        outcome.CompilationErrors.ShouldBeEmpty();
        outcome.Result.Results.SelectMany(r => r.GeneratedSources)
            .Select(s => s.HintName)
            .ShouldNotContain(n => n.Contains("Probe_Feature"));
    }

    [Fact]
    public void a_bad_bool_says_what_to_write_instead()
    {
        var message = run("a bool cell maybe").WithId("BOBCAT030").ShouldHaveSingleItem().GetMessage();
        message.ShouldContain("write true or false");
    }
}
