using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// bobcat#456: how a graph that is not flat is flattened, formatted and compared — a collection
/// member, a dictionary member, and a graph that refers back to itself.
/// </summary>
public class GraphComparisonTests
{
    public record Line(string Sku, int Quantity);

    public record Basket(string Id, List<Line> Lines);

    public record Headers(Dictionary<string, string> Values);

    public record ReadOnlyHeaders(IReadOnlyDictionary<string, int> Counts);

    public record Node(string Name)
    {
        public Node? Next { get; set; }
    }

    public record Branch(string Name)
    {
        public List<Branch> Children { get; } = [];
    }

    public abstract record Payment(decimal Amount);

    public record CardPayment(decimal Amount, string Last4) : Payment(Amount);

    public record CashPayment(decimal Amount) : Payment(Amount);

    public record Sale(string Id, Payment Paid);

    private static readonly Basket TheBasket = new("B1", [new Line("A", 1), new Line("B", 2)]);

    // ---- a collection member reads as its items, not as its type ------------------------------

    [Fact]
    public void a_collection_is_formatted_as_its_items()
    {
        // Was "System.Collections.Generic.List`1[...Line]" — the same text on both sides of a failure
        ObjectComparison.Format(TheBasket.Lines)
            .ShouldBe("[Line(Sku: \"A\", Quantity: 1), Line(Sku: \"B\", Quantity: 2)]");
    }

    [Fact]
    public void a_partial_collection_member_that_disagrees_names_the_item_that_disagreed()
    {
        // Was "Lines: expected List`1[Line], was List`1[Line]" — the same text on both sides
        var difference = PartialMatching
            .Differences(TheBasket, Specify<Basket>().With(x => x.Lines, new List<Line> { new("A", 1), new("B", 9) }))
            .ShouldHaveSingleItem();

        difference.Path.ShouldBe("Lines[1].Quantity");
        difference.Expected.ShouldBe(9);
        difference.Actual.ShouldBe(2);
    }

    [Fact]
    public void a_collection_is_matched_as_a_set_in_any_order()
    {
        ObjectComparison.Differences(TheBasket, TheBasket with { Lines = [new Line("B", 2), new Line("A", 1)] })
            .ShouldBeEmpty();
    }

    [Fact]
    public void expect_exactly_is_how_a_specification_claims_the_order()
    {
        var difference = PartialMatching
            .Differences(TheBasket, Specify<Basket>().With(x => x.Lines, Expect.Exactly(new Line("B", 2), new Line("A", 1))))
            .ShouldHaveSingleItem();

        difference.Path.ShouldBe("Lines[0]");
        difference.Note.ShouldStartWith("Out of order: Line was appended at position 1");
    }

    [Fact]
    public void an_expectation_nothing_satisfied_is_missing()
    {
        var difference = ObjectComparison.Differences(new[] { "vip" }, new[] { "vip", "rush" })
            .ShouldHaveSingleItem();

        difference.Path.ShouldBe("value");
        difference.Note.ShouldBe("MISSING: nothing like this");
    }

    [Fact]
    public void an_item_nobody_expected_is_extra()
    {
        var difference = ObjectComparison.Differences(new[] { "vip", "rush" }, new[] { "vip" })
            .ShouldHaveSingleItem();

        difference.Path.ShouldBe("value[1]");
        difference.Note.ShouldBe("EXTRA: nothing expected this");
    }

    [Fact]
    public void the_right_item_with_a_wrong_value_is_the_member_that_disagreed_not_a_missing_beside_an_extra()
    {
        var difference = ObjectComparison
            .Differences(TheBasket, TheBasket with { Lines = [new Line("A", 1), new Line("B", 9)] })
            .ShouldHaveSingleItem();

        difference.Path.ShouldBe("Lines[1].Quantity");
        difference.Expected.ShouldBe(9);
        difference.Actual.ShouldBe(2);
    }

    [Fact]
    public void containing_allows_what_nobody_expected_and_none_of_forbids()
    {
        PartialMatching.Differences(TheBasket,
            Specify<Basket>().With(x => x.Lines, Expect.Containing(Specify<Line>().With(l => l.Sku, "A")))).ShouldBeEmpty();

        PartialMatching.Differences(TheBasket,
                Specify<Basket>().With(x => x.Lines, Expect.NoneOf(Specify<Line>().With(l => l.Sku, "B"))))
            .ShouldHaveSingleItem()
            .Note.ShouldBe("PRESENT: a Line like this was not expected");

        PartialMatching.Differences(TheBasket,
            Specify<Basket>().With(x => x.Lines, Expect.NoneOf(Specify<Line>().With(l => l.Sku, "Z")))).ShouldBeEmpty();
    }

    [Fact]
    public void no_items_is_how_a_specification_says_the_collection_is_empty()
    {
        PartialMatching.Differences(new Basket("B1", []), Specify<Basket>().With(x => x.Lines, Expect.NoItems()))
            .ShouldBeEmpty();

        PartialMatching.Differences(TheBasket, Specify<Basket>().With(x => x.Lines, Expect.NoItems()))
            .Count.ShouldBe(2);
    }

    [Fact]
    public void a_collection_member_may_be_written_as_a_table_of_one_row_per_element()
    {
        PartialMatching.Differences(TheBasket, Specify<Basket>().With(x => x.Lines, Expect.Rows("""
            | Sku | Quantity |
            | B   | 2        |
            | A   | 1        |
            """))).ShouldBeEmpty();

        PartialMatching.Differences(TheBasket, Specify<Basket>().With(x => x.Lines, Expect.Rows("""
            | Sku | Quantity |
            | B   | 9        |
            | A   | 1        |
            """))).ShouldHaveSingleItem().Path.ShouldBe("Lines[1].Quantity");
    }

    [Fact]
    public void the_table_form_takes_the_order_as_an_argument_because_a_string_is_not_a_table_by_overload()
    {
        // Expect.Containing("vip") must stay a collection of one string, not a table with no rows
        PartialMatching.Differences(TheBasket, Specify<Basket>().With(x => x.Lines, Expect.Rows("""
                | Sku | Quantity |
                | B   | 2        |
                | A   | 1        |
                """, SetMode.Ordered)))
            .ShouldHaveSingleItem()
            .Note.ShouldStartWith("Out of order");
    }

    [Fact]
    public void a_collection_of_scalars_is_a_set_too()
    {
        ObjectComparison.Differences(new[] { "vip", "rush" }, new[] { "rush", "vip" }).ShouldBeEmpty();

        ObjectComparison.Differences(new[] { "vip" }, Expect.Containing("rush"))
            .ShouldHaveSingleItem()
            .Note.ShouldBe("MISSING: nothing like this");
    }

    // ---- Shouldly's ShouldBe over a collection -------------------------------------------------

    [Fact]
    public void should_be_over_a_collection_is_ordered_because_shouldly_says_so()
    {
        PartialMatching.Differences(TheBasket,
            Specify<Basket>(x => x.Lines.ShouldBe(new List<Line> { new("A", 1), new("B", 2) }))).ShouldBeEmpty();

        PartialMatching.Differences(TheBasket,
                Specify<Basket>(x => x.Lines.ShouldBe(new List<Line> { new("B", 2), new("A", 1) })))
            .ShouldHaveSingleItem()
            .Note.ShouldStartWith("Out of order");
    }

    [Fact]
    public void should_be_with_ignore_order_is_shouldlys_own_opt_out()
    {
        PartialMatching.Differences(TheBasket,
            Specify<Basket>(x => x.Lines.ShouldBe(new List<Line> { new("B", 2), new("A", 1) }, true))).ShouldBeEmpty();
    }

    [Fact]
    public void should_be_over_a_collection_compares_member_by_member_and_not_through_equals()
    {
        // Shouldly's own ShouldBe would compare these by reference, since Holder is a class
        PartialMatching.Differences(new Bag([new Holder("A")]),
            Specify<Bag>(x => x.Items.ShouldBe(new List<Holder> { new("A") }))).ShouldBeEmpty();
    }

    public class Holder(string Sku)
    {
        public string Sku { get; } = Sku;
    }

    public record Bag(List<Holder> Items);

    [Fact]
    public void a_collection_expectation_says_what_a_collection_must_hold_not_what_to_build()
    {
        Should.Throw<SpecCriticalException>(() =>
                Specify<Basket>().With(x => x.Lines, Expect.NoItems()).Build())
            .Message.ShouldContain("cannot be built");
    }

    // ---- a dictionary member is keyed ---------------------------------------------------------

    [Fact]
    public void a_dictionary_is_flattened_by_key()
    {
        ObjectComparison.Leaves(new Headers(new Dictionary<string, string> { ["accept"] = "json", ["auth"] = "token" }))
            .Select(x => x.Path)
            .ShouldBe(["Values[accept]", "Values[auth]", "Values.Count"]);
    }

    [Fact]
    public void two_dictionaries_holding_the_same_entries_agree_whatever_order_they_enumerate_in()
    {
        var one = new Headers(new Dictionary<string, string> { ["accept"] = "json", ["auth"] = "token" });
        var other = new Headers(new Dictionary<string, string> { ["auth"] = "token", ["accept"] = "json" });

        ObjectComparison.Differences(one, other).ShouldBeEmpty();
    }

    [Fact]
    public void a_key_the_actual_dictionary_lacks_is_one_difference_naming_that_key()
    {
        var actual = new Headers(new Dictionary<string, string> { ["accept"] = "json" });
        var expected = new Headers(new Dictionary<string, string> { ["accept"] = "json", ["auth"] = "token" });

        var difference = ObjectComparison.Differences(actual, expected).ShouldHaveSingleItem();

        difference.Path.ShouldBe("Values[auth]");
        difference.Expected.ShouldBe("token");
        difference.Actual.ShouldBeNull();
        difference.Note.ShouldBe("MISSING: no entry under 'auth'");
    }

    [Fact]
    public void a_read_only_dictionary_is_keyed_too()
    {
        ObjectComparison.Leaves(new ReadOnlyHeaders(new Dictionary<string, int> { ["a"] = 1 }.AsReadOnly()))
            .Select(x => x.Path)
            .ShouldBe(["Counts[a]", "Counts.Count"]);
    }

    [Fact]
    public void a_list_of_pairs_is_an_ordered_list_and_not_a_dictionary()
    {
        // It may hold one key twice, so reading it by key would lose its order and drop a duplicate
        ObjectComparison.Leaves(new List<KeyValuePair<string, int>> { new("a", 1), new("a", 2) })
            .Select(x => x.Path)
            .ShouldBe(["[0].Key", "[0].Value", "[1].Key", "[1].Value", "value.Count"]);
    }

    // ---- a graph that refers back to itself ---------------------------------------------------

    [Fact]
    public void a_cycle_is_one_leaf_saying_so()
    {
        var head = new Node("a");
        head.Next = head;

        var leaves = ObjectComparison.Leaves(head);

        leaves.Select(x => x.Path).ShouldBe(["Name", "Next"]);
        leaves[1].Value.ShouldBe("<cycle back to Node>");
    }

    [Fact]
    public void two_graphs_with_the_same_cycle_agree()
    {
        Node cyclic(string name)
        {
            var head = new Node(name);
            head.Next = head;
            return head;
        }

        ObjectComparison.Differences(cyclic("a"), cyclic("a")).ShouldBeEmpty();
        ObjectComparison.Differences(cyclic("a"), cyclic("b")).ShouldNotBeEmpty();
    }

    [Fact]
    public void the_same_child_twice_is_not_a_cycle()
    {
        var shared = new Line("A", 1);

        ObjectComparison.Leaves(new Basket("B1", [shared, shared]))
            .Select(x => x.Path)
            .ShouldBe(["Id", "Lines[0].Sku", "Lines[0].Quantity", "Lines[1].Sku", "Lines[1].Quantity", "Lines.Count"]);
    }

    [Fact]
    public void a_cycle_through_a_collection_terminates_too()
    {
        // The items of a collection are compared through PartialMatching, which comes back into this
        // comparison — so the ancestors have to be shared across the whole of it, not per call
        Branch cyclic()
        {
            var head = new Branch("a");
            head.Children.Add(head);
            return head;
        }

        ObjectComparison.Differences(cyclic(), cyclic()).ShouldBeEmpty();
    }

    // ---- a member of the wrong type --------------------------------------------------------------

    [Fact]
    public void a_member_of_the_wrong_type_is_one_difference_and_not_a_cascade()
    {
        var difference = ObjectComparison
            .Differences(new Sale("S1", new CashPayment(20m)), new Sale("S1", new CardPayment(20m, "1111")))
            .ShouldHaveSingleItem();

        difference.Path.ShouldBe("Paid");
        difference.Note.ShouldBe("Expected a CardPayment, was a CashPayment");
    }

    [Fact]
    public void naming_members_of_a_base_type_never_checks_the_runtime_type()
    {
        // A dotted path asks about a member, not about which type carries it
        PartialMatching.Differences(new Sale("S1", new CashPayment(20m)),
            Specify<Sale>().With(x => x.Paid.Amount, 20m)).ShouldBeEmpty();
    }

    [Fact]
    public void a_list_and_an_array_of_the_same_elements_are_the_same_claim()
    {
        ObjectComparison.Differences(new ProbeHolder([new Line("A", 1)]), new ProbeHolder(new List<Line> { new("A", 1) }))
            .ShouldBeEmpty();
    }

    public record ProbeHolder(IReadOnlyList<Line> Lines);

    [Fact]
    public void formatting_a_cyclic_object_does_not_recurse_through_its_own_ToString()
    {
        var head = new Node("a");
        head.Next = head;

        // The record's generated ToString walks the graph; Describe stops at its own depth
        ObjectComparison.Format(head).ShouldStartWith("Node(Name: \"a\"");
    }
}
