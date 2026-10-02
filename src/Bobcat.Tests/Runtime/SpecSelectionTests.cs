using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #391: the specifications a run was asked for, as a set of identities — so a monitor
/// names identities and never a framework's own filter.
/// </summary>
public class SpecSelectionTests
{
    [Fact]
    public void a_selection_admits_what_it_names_and_nothing_else()
    {
        var selection = SpecSelection.Of("Orders/places an order", "Stock/counts");

        selection.Includes("Orders/places an order").ShouldBeTrue();
        selection.Includes("Stock/counts").ShouldBeTrue();
        selection.Includes("Orders/cancels an order").ShouldBeFalse();
    }

    [Fact]
    public void it_admits_a_scenario_by_the_titles_it_is_identified_from()
    {
        // The identity is assembled from the two titles everywhere, so a caller holding a feature
        // and a scenario never has to spell the join itself.
        SpecSelection.Of("Orders/places an order")
            .Includes("Orders", "places an order")
            .ShouldBeTrue();
    }

    [Fact]
    public void an_empty_selection_narrows_nothing_and_admits_everything()
    {
        // Which is the ordinary unfiltered run — so a run path can take a selection
        // unconditionally rather than treating "no narrowing" as a separate case.
        SpecSelection.Everything.NarrowsAnything.ShouldBeFalse();
        SpecSelection.Everything.Includes("anything at all").ShouldBeTrue();

        SpecSelection.Of().ShouldBeSameAs(SpecSelection.Everything);
        SpecSelection.Of(["", "   "]).ShouldBeSameAs(SpecSelection.Everything);
    }

    [Fact]
    public void narrowing_is_distinguishable_from_matching_everything()
    {
        // The distinction a resident runner needs: MTP ignores a subset parameter it does not
        // understand and runs the whole suite, which looks exactly like a filter that matched
        // everything. A caller has to be able to tell "I asked for nothing in particular" from
        // "I asked, and it happens to cover the suite".
        SpecSelection.Of("Orders/places an order").NarrowsAnything.ShouldBeTrue();
    }

    [Fact]
    public void duplicates_collapse_and_the_order_asked_for_is_kept()
    {
        // Order is kept because it is the order a reason or a report lists them in; silently
        // reshuffling someone's request reads as a bug.
        SpecSelection.Of("Stock/counts", "Orders/places", "Stock/counts")
            .Identities
            .ShouldBe(["Stock/counts", "Orders/places"]);
    }

    [Fact]
    public void matching_is_exact_and_case_sensitive()
    {
        // A machine identity, not a search: DeclaredSteps is keyed ordinally and --filter-uid
        // matches exactly. Folding case here would let two scenarios differing only in case
        // collide into one — silently running the wrong spec, which is worse than a miss, because
        // a miss is reported.
        var selection = SpecSelection.Of("Orders/places an order");

        selection.Includes("orders/places an order").ShouldBeFalse();
        selection.Includes("Orders/Places An Order").ShouldBeFalse();
    }

    [Fact]
    public void it_names_what_the_suite_does_not_have()
    {
        // How a runner rejects a command with a reason, before running — rather than running a
        // narrowed suite that matched nothing and reporting a green run of zero tests.
        var selection = SpecSelection.Of("Orders/places an order", "Nope/not here", "Also/missing");

        selection.NotIn(["Orders/places an order", "Stock/counts"])
            .ShouldBe(["Nope/not here", "Also/missing"]);
    }

    [Fact]
    public void a_selection_that_narrows_nothing_rejects_nothing()
    {
        SpecSelection.Everything.NotIn(["Orders/places an order"]).ShouldBeEmpty();
    }
}
