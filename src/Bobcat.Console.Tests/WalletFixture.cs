using Bobcat;

// Names the Event Model this assembly's specs contribute slices to (issue #172). The same line a
// real spec assembly writes, and here it is load-bearing for the test rather than decoration:
// GET /api/event-model merges only the sources naming the CURRENT model, so the half this
// assembly publishes and the "host half" SpecEventModelPublishingTests pushes beside it have to
// agree on one name — which is the contract issue #294 says nothing checked.
[assembly: EventModelName("Wallets")]

namespace Bobcat.Console.Tests;

/// <summary>
/// The fixture behind <c>Features/Wallet.feature</c> — deliberately inert. Nothing about the
/// Event Model half depends on what a step DOES; the slice, its command and event roles, and the
/// <c>Specifications</c> that bind a scenario identity to it are all compile-time facts the
/// generator reads out of the step text. Steps that did real work would only add ways for the
/// end-to-end test to fail for reasons that have nothing to do with publishing.
/// </summary>
public class WalletFixture : Fixture
{
    [When("{command} is received")]
    public void CommandIsReceived(Type command)
    {
    }

    [Then("{event} is emitted")]
    public void EventIsEmitted(Type @event)
    {
    }
}

/// <summary>The command types <c>Wallet.feature</c> names. Their existence is the point.</summary>
public record CreditWallet(Guid WalletId, decimal Amount);

/// <inheritdoc cref="CreditWallet" />
public record DebitWallet(Guid WalletId, decimal Amount);

/// <summary>The event types <c>Wallet.feature</c> names.</summary>
public record WalletCredited(decimal Amount);

/// <inheritdoc cref="WalletCredited" />
public record WalletDebited(decimal Amount);
