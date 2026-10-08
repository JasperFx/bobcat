using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// BOBCAT032 (bobcat#415, bobcat#420): a constant table, or a constant partial-object path, whose
/// columns do not name members of the type it is about is a build error — so a renamed property
/// breaks the build instead of the next run.
/// </summary>
public class TableColumnDiagnosticTests
{
    private const string Types =
        """
        using System;
        using System.Collections.Generic;
        using Bobcat;
        using Bobcat.Runtime;

        namespace Specs;

        public record Order(Guid Id, string Customer, decimal Total, Address ShipTo);
        public record Address(string Street, string City);

        public sealed class Titled
        {
            [Header("Order #")] public string OrderNumber { get; set; } = "";
            public int Quantity { get; set; }
        }

        public class OpenBase { public string Name { get; set; } = ""; }
        public class Derived : OpenBase { public string Extra { get; set; } = ""; }

        public class Leaf { public string Name { get; set; } = ""; }
        """;

    private static GeneratorHarness.RunOutcome run(string body)
        => GeneratorHarness.Run(Types + "\n" + $$"""
            public class Specs : Fixture
            {
                public void Check(Order order, Titled titled, OpenBase open, object anything, List<Order> orders, Leaf leaf, string dynamicTable)
                {
            {{body}}
                }
            }
            """);

    private static IReadOnlyList<string> messages(GeneratorHarness.RunOutcome outcome)
        => outcome.WithId("BOBCAT032").Select(d => d.GetMessage()).ToList();

    [Fact]
    public void a_valid_property_check_is_clean()
    {
        var outcome = run("""
                          VerifyObject(order, "| Customer | Total | ShipTo.City |\n| Ann | 1 | Austin |");
                          """);

        messages(outcome).ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void a_misspelled_column_is_an_error_naming_what_exists()
    {
        var outcome = run("""
                          VerifyObject(order, "| Custmer |\n| Ann |");
                          """);

        var diagnostic = outcome.WithId("BOBCAT032").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("'Custmer' does not match Order");
        diagnostic.GetMessage().ShouldContain("Customer, Id, ShipTo, Total");
        diagnostic.Location.IsInSource.ShouldBeTrue();
    }

    [Fact]
    public void a_raw_string_table()
    {
        var outcome = run("""
                          VerifyObject(order, @"
                                | Customer | Totl |
                                | Ann      | 1    |");
                          """);

        messages(outcome).ShouldHaveSingleItem().ShouldContain("'Totl'");
    }

    [Fact]
    public void a_dotted_path_broken_partway_names_the_level_that_failed()
    {
        var outcome = run("""
                          VerifyObject(order, "| ShipTo.Town |\n| Austin |");
                          """);

        messages(outcome).ShouldHaveSingleItem().ShouldContain("no 'Town' on Address — it has City, Street");
    }

    [Fact]
    public void a_header_title_is_the_column_and_replaces_the_name()
    {
        messages(run("""VerifyObject(titled, "| Order # | Quantity |\n| A | 1 |");""")).ShouldBeEmpty();

        // A title REPLACES the name for a property check, as PropertyCells reads it.
        messages(run("""VerifyObject(titled, "| OrderNumber |\n| A |");""")).ShouldHaveSingleItem();
    }

    [Fact]
    public void a_nameof_table_is_checked_by_its_value()
    {
        var outcome = run("""
                          VerifyObject(order, $"| {nameof(Order.Customer)} | {nameof(Order.Total)} |\n| Ann | 1 |");
                          """);

        messages(outcome).ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void a_vertical_table_checks_its_fields()
    {
        var outcome = run("""
                          VerifyObject(order, "| field | value |\n| Customer | Ann |\n| Totall | 1 |");
                          """);

        messages(outcome).ShouldHaveSingleItem().ShouldContain("'Totall'");
    }

    [Fact]
    public void a_table_that_is_not_constant_is_left_to_the_run()
    {
        messages(run("VerifyObject(order, dynamicTable);")).ShouldBeEmpty();
    }

    [Fact]
    public void an_object_typed_subject_is_left_to_the_run()
    {
        messages(run("""VerifyObject(anything, "| Whatever |\n| 1 |");""")).ShouldBeEmpty();
    }

    [Fact]
    public void an_open_class_with_a_subclass_is_left_to_the_run()
    {
        // The runtime resolves against the actual type, and a Derived carries Extra.
        messages(run("""VerifyObject(open, "| Extra |\n| 1 |");""")).ShouldBeEmpty();
    }

    [Fact]
    public void a_class_nothing_derives_from_is_checked()
    {
        messages(run("""VerifyObject(leaf, "| Nmae |\n| 1 |");""")).ShouldHaveSingleItem();
    }

    [Fact]
    public void an_index_is_refused()
    {
        messages(run("""VerifyObject(order, "| ShipTo[0] |\n| 1 |");""")).ShouldHaveSingleItem()
            .ShouldContain("indexes into a collection");
    }

    [Fact]
    public void the_static_property_cells_entry_point()
    {
        messages(run("""PropertyCells.Verify(order, "| Totl |\n| 1 |");""")).ShouldHaveSingleItem();
    }

    [Fact]
    public void a_set_verification_checks_the_element_type()
    {
        messages(run("""VerifySet(orders, "| Customer | Total |\n| Ann | 1 |");""")).ShouldBeEmpty();
        messages(run("""VerifySet(orders, "| Customr |\n| Ann |");""")).ShouldHaveSingleItem().ShouldContain("'Customr'");
    }

    [Fact]
    public void a_set_verification_refuses_a_dotted_column()
    {
        messages(run("""VerifySet(orders, "| ShipTo.City |\n| Austin |");""")).ShouldHaveSingleItem()
            .ShouldContain("a set verification compares only top-level properties");
    }

    [Fact]
    public void a_bad_constant_key_column_is_an_error()
    {
        messages(run("""VerifySet(orders, "| Customer |\n| Ann |", keyColumns: "Customer, Idd");"""))
            .ShouldHaveSingleItem().ShouldContain("key column 'Idd'");
    }

    [Fact]
    public void the_static_set_comparer_entry_point()
    {
        messages(run("""SetVerificationComparer.Verify(orders, "| Totl |\n| 1 |");""")).ShouldHaveSingleItem();
    }

    [Fact]
    public void partial_object_tables_accept_constructor_parameters_and_check_every_header()
    {
        messages(run("""PartialObjects.FromTable(typeof(Order), "| customer | ShipTo.City |\n| Ann | Austin |");""")).ShouldBeEmpty();
        messages(run("""PartialObjects.FromTable(typeof(Order), "| Custmer |\n| Ann |");""")).ShouldHaveSingleItem();
    }

    [Fact]
    public void partial_object_tables_check_an_open_class_too()
    {
        // A partial object builds exactly T, so there is no subclass to defer to.
        messages(run("""PartialObjects.FromTable(typeof(OpenBase), "| Extra |\n| 1 |");""")).ShouldHaveSingleItem();
    }

    [Fact]
    public void a_constant_specified_path()
    {
        messages(run("""Specify<Order>().With(nameof(Order.Customer), "Ann").With("ShipTo.City", "Austin");""")).ShouldBeEmpty();
        messages(run("""Specify<Order>().With("Customr", "Ann");""")).ShouldHaveSingleItem().ShouldContain("path 'Customr'");
    }

    [Fact]
    public void an_expression_path_is_left_to_the_compiler()
    {
        messages(run("""Specify<Order>().With(x => x.Customer, "Ann");""")).ShouldBeEmpty();
    }
}
