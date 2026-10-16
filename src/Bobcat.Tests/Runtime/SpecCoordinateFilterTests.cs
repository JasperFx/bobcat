using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// bobcat#573: <c>bobcat run --slice/--chapter/--domain/--pattern/--trait</c>, resolved through the
/// spec manifest into identities — a repeated flag ORs, different flags AND, and a value nothing is
/// bound to is refused by name rather than run as an empty selection.
/// </summary>
public class SpecCoordinateFilterTests
{
    private static readonly SpecManifest manifest = new(
        SpecManifest.ProjectedLane, SpecManifest.XunitFramework, "BankSpecs",
        [
            new SpecManifestEntry("Banking/a deposit is accepted", "Bank.Specs", "a_deposit_is_accepted",
                null, "Deposit funds", "Money in", "Accounts", "Command"),
            new SpecManifestEntry("Banking/a deposit refund is issued", "Bank.Specs", "a_deposit_refund_is_issued",
                null, "Deposit refund", "Money in", "Accounts", "Command") { Traits = traits(("Category", "Slow")) },
            new SpecManifestEntry("Banking/the summary shows the balance", "Bank.Specs", "the_summary_shows_the_balance",
                null, "AccountSummary", "Reporting", "Accounts", "View"),
            new SpecManifestEntry("Plain/nothing about a model", "Plain.Specs", "nothing_about_a_model")
        ]);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> traits(params (string Name, string Value)[] pairs)
        => pairs.GroupBy(x => x.Name).ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Select(y => y.Value).ToList());

    private static IReadOnlyList<string> chosen(SpecCoordinateFilter filter)
    {
        var resolved = filter.Resolve(manifest);
        resolved.Refusal.ShouldBeNull();
        return resolved.Identities;
    }

    [Fact]
    public void no_flags_selects_everything()
    {
        new SpecCoordinateFilter().NarrowsAnything.ShouldBeFalse();
        chosen(new SpecCoordinateFilter()).Count.ShouldBe(4);
    }

    [Fact]
    public void a_slice_matches_whole_and_ignoring_case_never_as_a_substring()
    {
        // "Deposit funds" must not also pull in "Deposit refund"
        chosen(new SpecCoordinateFilter { Slices = ["deposit FUNDS"] })
            .ShouldBe(["Banking/a deposit is accepted"]);
    }

    [Fact]
    public void a_repeated_flag_ors()
    {
        chosen(new SpecCoordinateFilter { Slices = ["Deposit funds", "AccountSummary"] })
            .ShouldBe(["Banking/a deposit is accepted", "Banking/the summary shows the balance"]);
    }

    [Fact]
    public void different_flags_and()
    {
        chosen(new SpecCoordinateFilter { Chapters = ["Money in"], Patterns = ["Command"], Domains = ["Accounts"] })
            .ShouldBe(["Banking/a deposit is accepted", "Banking/a deposit refund is issued"]);

        chosen(new SpecCoordinateFilter { Domains = ["Accounts"], Patterns = ["View"] })
            .ShouldBe(["Banking/the summary shows the balance"]);
    }

    [Fact]
    public void a_trait_selects_on_declared_traits_and_on_the_coordinates_under_their_trait_names()
    {
        chosen(new SpecCoordinateFilter { Traits = ["category=slow"] })
            .ShouldBe(["Banking/a deposit refund is issued"]);

        // --trait Slice=X means what dotnet test --filter-trait "Slice=X" means
        chosen(new SpecCoordinateFilter { Traits = ["Slice=AccountSummary"] })
            .ShouldBe(["Banking/the summary shows the balance"]);

        chosen(new SpecCoordinateFilter { Chapters = ["Money in"], Traits = ["Category=Slow"] })
            .ShouldBe(["Banking/a deposit refund is issued"]);
    }

    [Fact]
    public void a_slice_no_specification_is_bound_to_is_refused_by_name_with_the_slices_there_are()
    {
        var resolved = new SpecCoordinateFilter { Slices = ["Withdraw funds", "Deposit funds"] }.Resolve(manifest);

        resolved.IsRefused.ShouldBeTrue();
        resolved.Identities.ShouldBeEmpty();
        resolved.Refusal.ShouldBe(
            "No specification is bound to the slice 'Withdraw funds'. The slices with specifications are: "
            + "AccountSummary, Deposit funds, Deposit refund.");
    }

    [Fact]
    public void every_unknown_value_is_named_not_just_the_first()
    {
        var resolved = new SpecCoordinateFilter { Chapters = ["Money out"], Patterns = ["Automation"] }.Resolve(manifest);

        resolved.Refusal!.ShouldContain("the chapter 'Money out'");
        resolved.Refusal.ShouldContain("the pattern 'Automation'");
    }

    [Fact]
    public void an_unknown_trait_is_refused_with_its_values_or_as_an_unknown_name()
    {
        new SpecCoordinateFilter { Traits = ["Category=Fast"] }.Resolve(manifest).Refusal
            .ShouldBe("No specification has the trait Category=Fast. Category is one of: Slow.");

        new SpecCoordinateFilter { Traits = ["Owner=me"] }.Resolve(manifest).Refusal
            .ShouldBe("No specification has a trait named 'Owner'.");
    }

    [Fact]
    public void a_trait_that_is_not_name_equals_value_is_refused()
    {
        new SpecCoordinateFilter { Traits = ["Category"] }.Resolve(manifest).Refusal
            .ShouldBe("--trait 'Category' is not Name=Value.");
    }

    [Fact]
    public void values_that_each_exist_but_never_together_are_refused_rather_than_run_as_nothing()
    {
        var resolved = new SpecCoordinateFilter { Slices = ["AccountSummary"], Patterns = ["Command"] }.Resolve(manifest);

        resolved.Refusal.ShouldBe(
            "No specification is slice 'AccountSummary' and pattern 'Command': each of those exists, but never together.");
    }

    [Fact]
    public void a_manifest_with_no_coordinates_says_so()
    {
        var plain = manifest with { Specs = [manifest.Specs[3]] };

        new SpecCoordinateFilter { Slices = ["Deposit funds"] }.Resolve(plain).Refusal
            .ShouldBe("No specification is bound to the slice 'Deposit funds' — no specification in BankSpecs names a slice at all.");
    }

    [Fact]
    public void coordinates_and_traits_round_trip_through_the_manifest_json()
    {
        var json = manifest.ToJson();
        json.ShouldContain("\"slice\": \"Deposit funds\"");
        json.ShouldContain("\"pattern\": \"View\"");

        var read = SpecManifest.FromJson(json)!;
        read.Specs[1].Slice.ShouldBe("Deposit refund");
        read.Specs[1].Chapter.ShouldBe("Money in");
        read.Specs[1].Traits!["Category"].ShouldBe(["Slow"]);

        // Null coordinates are left off, so a spec bound to nothing reads exactly as before
        read.Specs[3].Slice.ShouldBeNull();
        read.Specs[3].Traits.ShouldBeNull();
    }

    [Fact]
    public void coordinates_project_as_traits_in_a_fixed_order_leaving_out_nulls()
    {
        new SpecCoordinates("Deposit funds", null, "Accounts", "Command").ToTraits().ShouldBe(
        [
            new KeyValuePair<string, string>("Slice", "Deposit funds"),
            new KeyValuePair<string, string>("Domain", "Accounts"),
            new KeyValuePair<string, string>("Pattern", "Command")
        ]);
    }
}
