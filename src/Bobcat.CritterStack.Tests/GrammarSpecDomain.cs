using Bobcat;
using Bobcat.CritterStack;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Bobcat.CritterStack.Tests;

// --- the sample slice domain, uniquely named so the generator's {aggregate}/{command}/{event}/
//     {readmodel}/{message} captures resolve to exactly one type against this compilation. -------

public record OpenWallet(Guid WalletId, string Owner);
public record CreditWallet(Guid WalletId, decimal Amount);
public record DebitWallet(Guid WalletId, decimal Amount);

public record WalletOpened(Guid WalletId, string Owner);
public record WalletCredited(Guid WalletId, decimal Amount);
public record WalletDebited(Guid WalletId, decimal Amount);

/// <summary>Cascaded by the credit handler, so "Then WalletCreditedNotification is sent" has something to see.</summary>
public record WalletCreditedNotification(Guid WalletId);

/// <summary>
/// A field-less command (issue #233). Nothing on the wire means nothing to put in a table, and
/// the shipped act step has to accept that: an emlang import carries no field information at all,
/// so a scaffolded scenario's act is routinely this shape. It used to be a null-deref inside the
/// fixture.
/// </summary>
public record SweepWallets();

public static class SweepWalletsHandler
{
    public static void Handle(SweepWallets command)
    {
    }
}

/// <summary>Live-aggregated on read.</summary>
public class Wallet
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = "";
    public decimal Balance { get; set; }

    public void Apply(WalletOpened e) { Id = e.WalletId; Owner = e.Owner; }
    public void Apply(WalletCredited e) => Balance += e.Amount;
    public void Apply(WalletDebited e) => Balance -= e.Amount;
}

/// <summary>The async-projected read model the "read model contains" grammar asserts against.</summary>
public class WalletSummary
{
    public Guid Id { get; set; }
    public int Credits { get; set; }
    public decimal Balance { get; set; }

    public static WalletSummary Create(WalletOpened e) => new() { Id = e.WalletId };

    public void Apply(WalletCredited e)
    {
        Credits++;
        Balance += e.Amount;
    }
}

/// <summary>
/// The fan-out read model (issue #236): one document per <b>owner</b>, folding that owner's
/// wallets from every stream. Its id is the owner's name, not a stream id — which is precisely
/// what <c>Then the {readmodel} read model contains</c> cannot address, because that step loads
/// by the scenario's stream. A read model like this is half the read-model space, and before the
/// identity-bearing step the only ways to spec it were to redefine it as single-stream (a
/// modelling lie told to satisfy a grammar) or to leave it spec-less.
/// </summary>
public class OwnerWallets
{
    public string Id { get; set; } = "";
    public int Wallets { get; set; }
}

public class OwnerWalletsProjection : MultiStreamProjection<OwnerWallets, string>
{
    public OwnerWalletsProjection() => Identity<WalletOpened>(x => x.Owner);

    public void Apply(WalletOpened e, OwnerWallets view) => view.Wallets++;
}

public class WalletHandler
{
    public static async Task Handle(OpenWallet command, IDocumentStore store)
    {
        await using var session = store.LightweightSession();
        session.Events.StartStream<Wallet>(command.WalletId, new WalletOpened(command.WalletId, command.Owner));
        await session.SaveChangesAsync();
    }

    // Returns a cascading notification (published — "sent"), and enforces the sad-path rule the
    // "validation fails" grammar asserts on.
    public static async Task<WalletCreditedNotification> Handle(CreditWallet command, IDocumentStore store)
    {
        if (command.Amount <= 0)
            throw new InvalidOperationException("Credit amount must be positive");

        await using var session = store.LightweightSession();
        session.Events.Append(command.WalletId, new WalletCredited(command.WalletId, command.Amount));
        await session.SaveChangesAsync();

        return new WalletCreditedNotification(command.WalletId);
    }

    // Local sink so the cascaded notification routes cleanly and is tracked as sent + handled.
    public static void Handle(WalletCreditedNotification notification) { }
}

/// <summary>
/// Refuses the way Wolverine's messaging guidance recommends — a <c>Before</c> returning
/// <c>HandlerContinuation.Stop</c>, no exception anywhere — which is the railway
/// "Then the command is refused" exists to describe (issue #168). A handler written this way
/// can never satisfy "Then validation fails with …", because nothing ever throws.
/// </summary>
public class DebitWalletHandler
{
    public static async Task<HandlerContinuation> Before(DebitWallet command, IDocumentStore store)
    {
        await using var session = store.LightweightSession();
        var wallet = await session.Events.AggregateStreamAsync<Wallet>(command.WalletId);
        return wallet != null && wallet.Balance >= command.Amount
            ? HandlerContinuation.Continue
            : HandlerContinuation.Stop;
    }

    public static async Task Handle(DebitWallet command, IDocumentStore store)
    {
        await using var session = store.LightweightSession();
        session.Events.Append(command.WalletId, new WalletDebited(command.WalletId, command.Amount));
        await session.SaveChangesAsync();
    }
}

/// <summary>
/// A spec fixture that IS a Critter Stack fixture — the canonical base-class route. It declares no
/// steps of its own: every step in <c>Wallet.feature</c> is the shipped grammar inherited from
/// <see cref="CritterStackFixture"/>, discovered from the referenced assembly's metadata (issue #104).
/// </summary>
[FixtureTitle("Wallet")]
public class WalletFixture : CritterStackFixture;
