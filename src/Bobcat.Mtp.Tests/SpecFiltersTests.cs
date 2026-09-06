using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Mtp.Tests;

/// <summary>
/// The friendly-filter semantics (issue #207), pinned pure — they deliberately mirror
/// <c>BobcatRunner</c>'s own <c>--feature</c>/<c>--tag</c> filtering so a filter means the same
/// thing however a suite is driven.
/// </summary>
public class SpecFiltersTests
{
    private static FeatureDefinition feature(string title)
        => new(title, typeof(Fixture), []);

    private static ScenarioDefinition scenario(string title, params string[] tags)
        => new(title, tags, (_, _) => { });

    [Fact]
    public void no_filters_match_everything()
    {
        SpecFilters.Matches(feature("Ordering"), scenario("adds"), [], []).ShouldBeTrue();
    }

    [Fact]
    public void a_feature_filter_is_a_case_insensitive_substring_of_the_title()
    {
        SpecFilters.MatchesFeature("Order Aggregate", ["order"]).ShouldBeTrue();
        SpecFilters.MatchesFeature("Order Aggregate", ["aggreg"]).ShouldBeTrue();
        SpecFilters.MatchesFeature("Order Aggregate", ["shipping"]).ShouldBeFalse();
    }

    [Fact]
    public void several_feature_filters_are_or()
    {
        SpecFilters.MatchesFeature("Shipping", ["ordering", "shipping"]).ShouldBeTrue();
        SpecFilters.MatchesFeature("Billing", ["ordering", "shipping"]).ShouldBeFalse();
    }

    [Fact]
    public void a_tag_filter_matches_a_tag_exactly_and_case_insensitively()
    {
        SpecFilters.MatchesTags(["regression", "slow"], ["Regression"]).ShouldBeTrue();

        // Exact, not substring — "regress" is not a tag the scenario carries.
        SpecFilters.MatchesTags(["regression"], ["regress"]).ShouldBeFalse();
    }

    [Fact]
    public void a_leading_at_sign_on_the_filter_is_tolerated()
    {
        // Tags are stored without the @, but a user typing what the .feature file shows
        // should not be told the tag does not exist.
        SpecFilters.MatchesTags(["regression"], ["@regression"]).ShouldBeTrue();
    }

    [Fact]
    public void several_tag_filters_are_or()
    {
        SpecFilters.MatchesTags(["slow"], ["regression", "slow"]).ShouldBeTrue();
        SpecFilters.MatchesTags(["fast"], ["regression", "slow"]).ShouldBeFalse();
    }

    [Fact]
    public void feature_and_tag_filters_are_and()
    {
        var ordering = feature("Ordering");
        var tagged = scenario("emptied", "regression");

        SpecFilters.Matches(ordering, tagged, ["Ordering"], ["regression"]).ShouldBeTrue();
        SpecFilters.Matches(ordering, tagged, ["Shipping"], ["regression"]).ShouldBeFalse();
        SpecFilters.Matches(ordering, tagged, ["Ordering"], ["slow"]).ShouldBeFalse();
    }

    [Fact]
    public void absent_command_line_options_mean_no_filtering()
    {
        var (features, tags) = SpecFilters.From(null);
        features.ShouldBeEmpty();
        tags.ShouldBeEmpty();
    }
}
