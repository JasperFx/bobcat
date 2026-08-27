using Bobcat.Generated.EventModel;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// Issue #106 — the generator emits Event Modeling slice descriptors from Gherkin, and this
/// assembly's <c>Wallet.feature</c> is the proof: written only in shipped Critter Stack grammar,
/// it declares two slices with no hand-written builder anywhere.
/// </summary>
/// <remarks>
/// These run against the <em>generated</em> <c>BobcatEventModelSource</c>, so the test failing to
/// compile is itself the headline assertion: a renamed command type breaks the build at the
/// feature that names it.
/// </remarks>
public class EventModelDescriptorTests
{
    private static EventModelDescriptor describe() => BobcatEventModelSource.Describe();

    private static EventModelSliceDescriptor slice(string name)
        => describe().Slices.Single(s => s.Name == name);

    [Fact]
    public void the_model_is_named_for_the_assembly_and_carries_every_declared_slice()
    {
        var model = describe();
        model.Name.ShouldBe("Bobcat.CritterStack.Tests");
        model.Slices.Select(s => s.Name).ShouldBe(["OpenWallet", "CreditWallet", "DebitWallet"], ignoreOrder: true);
    }

    [Fact]
    public void a_slice_is_a_scenario_level_grouping_so_several_scenarios_fold_into_one()
    {
        // Wallet.feature tags three scenarios @slice:CreditWallet. A slice is a vertical
        // behaviour, not a document, so they are one descriptor with three specifications.
        slice("CreditWallet").Specifications.Count.ShouldBe(3);
        slice("OpenWallet").Specifications.Count.ShouldBe(1);
    }

    [Fact]
    public void the_domain_comes_from_the_feature_tag_and_is_inherited_by_every_scenario()
    {
        // @domain:Wallets sits on the Feature line; standard Gherkin inheritance carries it down.
        slice("OpenWallet").Domain.ShouldBe("Wallets");
        slice("CreditWallet").Domain.ShouldBe("Wallets");
    }

    [Fact]
    public void the_trigger_label_comes_from_the_triggered_by_description_line()
    {
        slice("OpenWallet").TriggerLabel.ShouldBe("the wallet holder");
    }

    [Fact]
    public void the_command_is_the_act_not_the_first_command_the_scenario_names()
    {
        // The CreditWallet scenarios arrange by issuing `When OpenWallet is received` first. The
        // last When is the act; taking the first labelled this slice with OpenWallet.
        slice("CreditWallet").CommandType!.Name.ShouldBe("CreditWallet");
        slice("OpenWallet").CommandType!.Name.ShouldBe("OpenWallet");
    }

    [Fact]
    public void an_arrange_command_still_reaches_the_specification_as_evidence()
    {
        // It is not the slice's command, but the spec did touch the type, and run evidence
        // (#107) joins on exactly this list.
        var spec = slice("CreditWallet").Specifications
            .Single(s => s.Identity.EndsWith("emits the credited event and sends a notification"));
        spec.ResolvedTypes.Select(t => t.Name).ShouldContain("OpenWallet");
        spec.ResolvedTypes.Select(t => t.Name).ShouldContain("CreditWallet");
    }

    [Fact]
    public void roles_land_in_their_own_slots_rather_than_one_bag_of_types()
    {
        var credit = slice("CreditWallet");
        credit.EmittedEvents.Select(t => t.Name).ShouldBe(["WalletCredited"]);
        credit.AggregateTypes.Select(t => t.Name).ShouldBe(["Wallet"]);
        credit.ReadModelTypes.Select(t => t.Name).ShouldBe(["WalletSummary"]);
        // A published message is kept apart from the events so the event lane shows only events.
        credit.PublishedMessages.Select(t => t.Name).ShouldBe(["WalletCreditedNotification"]);
    }

    [Fact]
    public void the_spec_identity_is_the_same_string_the_runner_uses_as_a_test_id()
    {
        // {Feature}/{Scenario} — the same key SpecNodeMapping.Uid produces and the same one
        // scenario_finished carries, so design-time and run evidence join without a mapping table.
        slice("OpenWallet").Specifications.Single().Identity
            .ShouldBe("Wallet/Opening a wallet emits the opened event and starts an empty balance");
    }

    [Fact]
    public void the_pattern_is_derived_where_gherkin_can_tell_and_left_null_where_it_cannot()
    {
        // A slice that receives a command is a Command slice. Automation and Translation need a
        // trigger Gherkin does not express, so they are never guessed.
        slice("CreditWallet").Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public void the_rendering_contract_is_computed_upstream_from_the_roles_we_stamped()
    {
        // Elements/Edges are computed by JasperFx on read, which is why the generator stamps only
        // roles — building a graph here would be a second opinion about the same slice.
        var elements = slice("CreditWallet").Elements;
        elements.ShouldNotBeEmpty();
        elements.Select(e => e.Kind).ShouldContain(EventModelElementKind.Command);
        elements.Select(e => e.Kind).ShouldContain(EventModelElementKind.Event);
        elements.Where(e => e.Kind == EventModelElementKind.Event)
            .Select(e => e.Lane).ShouldAllBe(lane => lane == EventModelLane.EventStream);
    }

    [Fact]
    public async Task the_source_surfaces_the_descriptor_through_the_jasperfx_interface()
    {
        // The acceptance criterion: a host reports its slices through IEventModelDefinitionSource
        // with no hand-written builder.
        IEventModelDefinitionSource source = BobcatEventModelSource.Instance;
        source.Subject.ShouldBe(new Uri("event-model://Bobcat.CritterStack.Tests"));

        var descriptor = await source.TryCreateAsync(null!, TestContext.Current.CancellationToken);
        descriptor.ShouldNotBeNull();
        descriptor.Slices.Count.ShouldBe(3);
    }
}
