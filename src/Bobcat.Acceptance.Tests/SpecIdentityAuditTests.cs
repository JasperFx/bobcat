using Shouldly;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Issue #338, end to end: the identities this assembly's REAL generated descriptor declares,
/// audited against a design model.
/// </summary>
/// <remarks>
/// The pure comparison is covered in <c>Bobcat.Tests</c>. What only this project can prove is the
/// half that actually broke in CritterCrush — that the join sees what the generator emitted, out
/// of a compiled spec assembly, through the internal generated type. <c>SliceIdentity.feature</c>
/// and its fixture exist for this test and nothing else.
/// </remarks>
public class SpecIdentityAuditTests
{
    private const string Credited = "Slice Identity/A credited wallet shows the new balance";
    private const string Refused = "Slice Identity/Crediting nothing is refused";
    private const string Unspecified = "Slice Identity/The balance view is not specified yet";

    /// <summary>
    /// The identity this project's OTHER slice-tagged feature declares. It is not noise — it is
    /// why the audit has to be told what a model does not cover, rather than assuming a spec
    /// assembly describes exactly one model.
    /// </summary>
    private const string Elsewhere = "Derived Calculator/The derived step hides the base step of the same text";

    private static IReadOnlyList<SpecIdentityClaim> compiled()
    {
        var descriptor = GeneratedEventModel.For(typeof(SpecIdentityAuditTests).Assembly);
        descriptor.ShouldNotBeNull();
        return SpecIdentityAudit.ClaimsIn(descriptor);
    }

    /// <summary>Only the feature this test owns — see <see cref="Elsewhere"/>.</summary>
    private static IReadOnlyList<SpecIdentityClaim> compiledHere()
        => compiled().Where(x => x.Identity.StartsWith("Slice Identity/")).ToList();

    /// <summary>A design model declaring exactly the identities named, on the slices named.</summary>
    private static EventModelDescriptor design(params (string Slice, string Identity)[] scenarios)
        => new("Acceptance", scenarios
            .GroupBy(x => x.Slice)
            .Select(group => new EventModelSliceDescriptor(
                group.Key, null, null, null, null,
                [], [], [])
            {
                Specifications = group.Select(x => new SpecificationDescriptor(x.Identity)).ToList(),
                Hotspots = []
            })
            .ToList());

    [Fact]
    public void the_generated_descriptor_is_reachable_out_of_a_compiled_spec_assembly()
    {
        var claims = compiled();

        // The two specified scenarios bind to the slice their tag names…
        claims.ShouldContain(x => x.Identity == Credited && x.Slice == "CreditWallet" && !x.Pending);
        claims.ShouldContain(x => x.Identity == Refused && x.Slice == "CreditWallet" && !x.Pending);

        // …and the step-less one is a claim too, marked pending. Leaving it out would report the
        // scenario as a hole — "nothing verifies this" — when the test is right there, empty.
        claims.ShouldContain(x => x.Identity == Unspecified && x.Slice == "WalletBalance" && x.Pending);
    }

    [Fact]
    public void a_model_that_declares_every_identity_has_no_drift()
    {
        var audit = SpecIdentityAudit.Compare(
            SpecIdentityAudit.ClaimsIn(design(
                ("CreditWallet", Credited),
                ("CreditWallet", Refused),
                ("WalletBalance", Unspecified))),
            compiledHere());

        audit.Drifted.ShouldBeFalse();
        audit.Matched.ShouldBe(3);
        audit.Pending.ShouldHaveSingleItem().Identity.ShouldBe(Unspecified);
        audit.Report().ShouldContain("3 matched, no drift");
    }

    [Fact]
    public void a_scenario_nobody_designed_is_an_orphan()
    {
        // The drift that survived four commits: a spec added, the model not updated.
        var audit = SpecIdentityAudit.Compare(
            SpecIdentityAudit.ClaimsIn(design(("CreditWallet", Credited), ("WalletBalance", Unspecified))),
            compiledHere());

        audit.Drifted.ShouldBeTrue();
        audit.Orphans.ShouldHaveSingleItem().Identity.ShouldBe(Refused);
        audit.Holes.ShouldBeEmpty();
        audit.Report().ShouldContain("Orphans");
        audit.Report().ShouldContain(Refused);
    }

    [Fact]
    public void a_designed_scenario_nothing_covers_is_a_hole()
    {
        var audit = SpecIdentityAudit.Compare(
            SpecIdentityAudit.ClaimsIn(design(
                ("CreditWallet", Credited),
                ("CreditWallet", Refused),
                ("WalletBalance", Unspecified),
                ("CreditWallet", "Slice Identity/Crediting a frozen wallet is refused"))),
            compiledHere());

        audit.Drifted.ShouldBeTrue();
        audit.Holes.ShouldHaveSingleItem().Identity.ShouldBe("Slice Identity/Crediting a frozen wallet is refused");
        audit.Orphans.ShouldBeEmpty();
    }

    [Fact]
    public void the_assembly_form_is_what_a_spec_project_calls()
    {
        // One line in the spec assembly's own test suite, which is the only place both facts are
        // already loaded. `excused` names what is deliberately unbound, out loud.
        var audit = SpecIdentityAudit.Compare(
            design(("CreditWallet", Credited), ("CreditWallet", Refused)),
            new Dictionary<string, string>
            {
                [Unspecified] = "the balance view is specified by a projected xUnit test",
                [Elsewhere] = "another model's slice, in the same spec assembly"
            },
            typeof(SpecIdentityAuditTests).Assembly);

        audit.Drifted.ShouldBeFalse();
        audit.Excused.Select(x => x.Key).ShouldBe([Elsewhere, Unspecified]);
        audit.Report().ShouldContain("specified by a projected xUnit test");
    }
}
