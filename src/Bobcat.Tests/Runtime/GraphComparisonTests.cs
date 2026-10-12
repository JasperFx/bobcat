using Bobcat;
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
    public void a_partial_collection_member_that_disagrees_says_what_each_side_held()
    {
        var difference = PartialMatching
            .Differences(TheBasket, Specify<Basket>().With(x => x.Lines, new List<Line> { new("A", 1), new("B", 9) }))
            .ShouldHaveSingleItem();

        difference.Path.ShouldBe("Lines");
        ObjectComparison.Format(difference.Expected).ShouldContain("Quantity: 9");
        ObjectComparison.Format(difference.Actual).ShouldContain("Quantity: 2");
    }

    [Fact]
    public void a_collection_is_still_compared_position_by_position()
    {
        ObjectComparison.Differences(TheBasket, TheBasket with { Lines = [new Line("B", 2), new Line("A", 1)] })
            .Select(x => x.Path)
            .ShouldBe(["Lines[0].Sku", "Lines[0].Quantity", "Lines[1].Sku", "Lines[1].Quantity"]);
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

        var differences = ObjectComparison.Differences(actual, expected);

        differences.Select(x => x.Path).ShouldBe(["Values[auth]", "Values.Count"]);
        differences[0].Expected.ShouldBe("token");
        differences[0].Actual.ShouldBeNull();
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
    public void formatting_a_cyclic_object_does_not_recurse_through_its_own_ToString()
    {
        var head = new Node("a");
        head.Next = head;

        // The record's generated ToString walks the graph; Describe stops at its own depth
        ObjectComparison.Format(head).ShouldStartWith("Node(Name: \"a\"");
    }
}
