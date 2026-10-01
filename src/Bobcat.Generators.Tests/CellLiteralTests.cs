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

            [Given("a string cell {word}")]
            public void StringCell(string s) { }
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
    public void a_date_is_read_at_run_time_so_TODAY_means_the_day_of_the_run()
    {
        // Validated at compile time and read at run time. Baking the date in would give a build
        // cached overnight yesterday's answer on every later run, with nothing to say so.
        run("a datetime cell 2026-01-01").GeneratedSource("Probe_Feature")
            .ShouldContain("global::Bobcat.Runtime.CellValues.Read<global::System.DateTime>(\"2026-01-01\")");
    }

    [Fact]
    public void a_relative_date_cell_reads_as_one()
    {
        var outcome = run("a datetime cell TODAY+2");

        outcome.WithId("BOBCAT030").ShouldBeEmpty();
        outcome.GeneratedSource("Probe_Feature")
            .ShouldContain("global::Bobcat.Runtime.CellValues.Read<global::System.DateTime>(\"TODAY+2\")");
    }

    [Fact]
    public void a_relative_time_against_a_string_is_just_the_text()
    {
        // A string cell is text. A table entitled to keep its dates as text says TODAY and means it.
        run("a string cell TODAY").GeneratedSource("Probe_Feature").ShouldContain("StringCell(\"TODAY\")");
    }

    [Fact]
    public void a_relative_time_against_a_number_is_refused_as_a_number()
    {
        run("an int cell TODAY").WithId("BOBCAT030").ShouldHaveSingleItem()
            .GetMessage().ShouldContain("'TODAY' is not an int");
    }

    [Fact]
    public void the_reserved_tokens_mean_on_the_input_side_what_they_mean_on_the_expected_side()
    {
        var source = run("a nullable int cell NULL", "a string cell EMPTY").GeneratedSource("Probe_Feature");

        source.ShouldContain("NullableIntCell(null)");
        source.ShouldContain("StringCell(\"\")");
    }

    [Fact]
    public void a_quoted_token_is_the_literal_text_of_it()
    {
        run("a string cell \"NULL\"").GeneratedSource("Probe_Feature")
            .ShouldContain("global::Bobcat.Runtime.CellValues.Read<string>");
    }

    [Fact]
    public void NULL_against_a_type_that_cannot_be_null_says_so()
    {
        run("an int cell NULL").WithId("BOBCAT030").ShouldHaveSingleItem()
            .GetMessage().ShouldContain("int cannot be null");
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
