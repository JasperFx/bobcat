using Bobcat.Xunit;

// Names the Event Model this assembly's specs contribute slices to (issue #172). The same line a
// real spec assembly writes, and here it is load-bearing for the test rather than decoration:
// GET /api/event-model merges only the sources naming the CURRENT model, so the half this
// assembly declares and the "host half" SpecEventModelPublishingTests stores beside it have to
// agree on one name -- which is the contract issue #294 says nothing checked.
[assembly: EventModelName("Wallets")]

namespace Bobcat.Ui.Tests;

/// <summary>
/// A spec assembly's half of an Event Model, for real (issue #294). There is nothing special about
/// these specs: they exist so that THIS assembly carries a generated <c>BobcatEventModelSource</c> with
/// slices and Specifications in it, which is the artefact <see cref="SpecEventModelPublishingTests"/>
/// sends into a real <c>EventModelStore</c> and reads back out as half of a merge.
/// </summary>
/// <remarks>
/// <para>
/// This was Features/Wallet.feature until Bobcat 0.50.0 removed the Gherkin lane. The projected twin
/// declares the same half: the feature title, the domain, the trigger line, a slice per spec, and the
/// command and event roles -- off the two steps' <c>[EventModelRole]</c> attributes, where the feature's
/// <c>{command}</c> and <c>{event}</c> captures stamped them before.
/// </para>
/// <para>
/// The steps are deliberately inert. Nothing about the Event Model half depends on what a step DOES;
/// the slice, its roles and the Specifications that bind a spec identity to it are all compile-time
/// facts the generator reads off the calls. Steps that did real work would only add ways for the
/// end-to-end test to fail for reasons that have nothing to do with the model.
/// </para>
/// </remarks>
[BobcatFeature("Wallet")]
public class WalletSpecs
{
    [BobcatSpec(typeof(CreditWallet), Domain = "Wallets", TriggeredBy = "a teller working the counter")]
    public void Crediting_a_wallet_emits_the_credited_event()
    {
        WhenReceived(new CreditWallet(Guid.NewGuid(), 10m));
        ThenIsEmitted(typeof(WalletCredited));
    }

    [BobcatSpec(typeof(DebitWallet), Domain = "Wallets", TriggeredBy = "a teller working the counter")]
    public void Debiting_a_wallet_emits_the_debited_event()
    {
        WhenReceived(new DebitWallet(Guid.NewGuid(), 10m));
        ThenIsEmitted(typeof(WalletDebited));
    }

    [When("{command} is received")]
    [EventModelRole(SliceRole.Command, nameof(command))]
    internal void WhenReceived(object command)
    {
    }

    [Then("{event} is emitted")]
    [EventModelRole(SliceRole.Event, nameof(@event))]
    internal void ThenIsEmitted(Type @event)
    {
    }
}

/// <summary>The command types the wallet specs name. Their existence is the point.</summary>
public record CreditWallet(Guid WalletId, decimal Amount);

/// <inheritdoc cref="CreditWallet" />
public record DebitWallet(Guid WalletId, decimal Amount);

/// <summary>The event types the wallet specs name.</summary>
public record WalletCredited(decimal Amount);

/// <inheritdoc cref="WalletCredited" />
public record WalletDebited(decimal Amount);
