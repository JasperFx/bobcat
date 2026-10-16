using Bobcat.Xunit;

namespace Bobcat.Ui.Specs;

/// <summary>
/// The design-time descriptor wire (issue #108): a producer pushes the current Event Model over
/// PUT /api/event-model -- Wolverine's event-model export, or what a spec assembly's generated
/// IEventModelDefinitionSource reported -- and the viewer's Event Model page reads it back over
/// GET /api/event-model, the same public wire an outside consumer uses.
/// </summary>
[BobcatFeature("Event Model"), BobcatScenario]
public class EventModelSpecs(MonitorHost host) : ViewerSpec(host)
{
    private readonly MonitorHost _host = host;

    /// <summary>Every test starts from a viewer that has never been sent a model.</summary>
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await _host.ForgetEventModelAsync();
    }

    [Fact]
    public async Task Nothing_published_yet_reads_as_absent_not_empty()
    {
        await ThenAskingForTheEventModelRespondsWith(404);
    }

    [Fact]
    public async Task A_pushed_descriptor_is_served_back_normalized()
    {
        await WhenTheEventModelIsPublished("Wallets", "CreditWallet", "Wallet/Crediting a wallet");
        await ThenAskingForTheEventModelRespondsWith(200);
        await ThenThePublishedEventModelIsNamed("Wallets");
        await ThenTheSliceCarriesTheSpec("CreditWallet", "Wallet/Crediting a wallet");
    }

    // CritterWatch#1212 -- the two halves of a model are compiled into different assemblies: the host's
    // event-model export carries slices with no specs, a spec assembly's generated source carries the
    // specs. Latest-wins erased one of them, so nothing could ever be joined to a run outcome.
    [Fact]
    public async Task Two_producers_each_publish_their_half_and_the_slices_fold_together()
    {
        await WhenTheEventModelIsPublishedWithoutSpec("Wallets", "CreditWallet");
        await WhenTheSourcePublishesTheEventModel("specs", "Wallets", "CreditWallet", "Wallet/Crediting a wallet");
        await ThenTheSliceCarriesTheSpec("CreditWallet", "Wallet/Crediting a wallet");
        await ThenTheSliceStillCarriesItsCommandType("CreditWallet");
    }

    // A re-push must REPLACE that source, not add to it -- otherwise a producer that renames or drops
    // a slice leaves the old one behind forever and the model drifts from the code with every push.
    [Fact]
    public async Task Re_publishing_a_source_replaces_that_source_rather_than_accumulating()
    {
        await WhenTheSourcePublishesTheEventModel("specs", "Wallets", "CreditWallet", "Wallet/First");
        await WhenTheSourcePublishesTheEventModel("specs", "Wallets", "CreditWallet", "Wallet/Second");
        await ThenTheSliceCarriesTheSpec("CreditWallet", "Wallet/Second");
        await ThenTheSliceDoesNotCarryTheSpec("CreditWallet", "Wallet/First");
    }

    // bobcat#298 -- the chapter is a role of the slice (jasperfx#824), so a pushed document keeps it
    // through the store's normalization and GET serves it back, ready for the canvas's chapter bands.
    [Fact]
    public async Task A_pushed_descriptor_keeps_its_chapters()
    {
        await WhenTheEventModelIsPublishedWithChapter("Wallets", "CreditWallet", "Everyday banking");
        await ThenTheSliceIsInChapter("CreditWallet", "Everyday banking");
    }

    [Fact]
    public async Task A_successful_push_is_broadcast_so_an_open_page_redraws_without_an_F5()
    {
        await WhenTheEventModelIsPublished("Wallets", "CreditWallet", "Wallet/Crediting a wallet");
        ThenTheEventModelChangeIsBroadcast("Wallets");
    }

    // Nothing about what the page would load has moved, so announcing a change would make an open
    // diagram flicker for a document nobody stored. (Deliberately asserts only the broadcast, as the
    // Gherkin scenario did when what GET returned here depended on what earlier scenarios published.)
    [Fact]
    public async Task A_rejected_push_announces_nothing()
    {
        await WhenSomethingThatIsNotAnEventModelIsPublished();
        ThenNoEventModelChangeIsBroadcast();
    }
}
