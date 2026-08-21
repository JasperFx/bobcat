using Bobcat.Engine;
using Bobcat.Marten.Tests;
using Bobcat.Runtime;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// The store-agnostic helpers against a real store: Marten on the repo's Postgres (5445), with a
/// Wolverine host dispatching the commands and Marten's async daemon running a projection. Note
/// what this project references and what <c>Bobcat.CritterStack</c> does not — every call below
/// goes through <c>JasperFx.Events.IEventStore</c> resolved from the host, never through
/// <c>IDocumentStore</c>. See <see cref="PostgresFactAttribute"/> for how a missing database is
/// handled.
/// </summary>
public class MartenIntegrationTests
{
    private const string schema = "bobcat_critterstack";
    private static readonly TimeSpan projectionTimeout = TimeSpan.FromSeconds(20);

    [PostgresFact]
    public async Task execute_aggregate_command_returns_the_new_events_and_the_rebuilt_aggregate()
    {
        await cleanSchema();
        await using var resource = hostResource();
        await resource.Start();
        var context = contextFor(resource);
        var id = Guid.NewGuid();

        var opened = await context.ExecuteAggregateCommandAsync<Account>(new OpenAccount(id, "Ann"), id);
        opened.Session.ShouldNotBeNull();
        opened.NewEvents.Select(e => e.Data).ShouldHaveSingleItem().ShouldBeOfType<AccountOpened>();
        opened.Aggregate.ShouldNotBeNull();
        opened.Aggregate.Owner.ShouldBe("Ann");

        var deposited = await context.ExecuteAggregateCommandAsync<Account>(new Deposit(id, 50m), id);
        deposited.NewEvents.Select(e => e.Data).ShouldHaveSingleItem().ShouldBeOfType<Deposited>();
        deposited.Aggregate!.Balance.ShouldBe(50m);

        (await context.FetchEventStreamAsync(id)).Count.ShouldBe(2);
        (await context.AggregateEventStreamAsync<Account>(id))!.Balance.ShouldBe(50m);
    }

    [PostgresFact]
    public async Task waits_for_the_async_projection_to_catch_up()
    {
        await cleanSchema();
        await using var resource = hostResource();
        await resource.Start();
        var context = contextFor(resource);
        var id = Guid.NewGuid();

        await context.ExecuteAggregateCommandAsync<Account>(new OpenAccount(id, "Bea"), id);
        await context.ExecuteAggregateCommandAsync<Account>(new Deposit(id, 10m), id);
        await context.ExecuteAggregateCommandAsync<Account>(new Deposit(id, 15m), id);

        // Both spellings of the wait, both through IEventDatabase — no IDocumentStore involved.
        await context.WaitForNonStaleProjectionsAsync(projectionTimeout);
        await context.WaitForProjectionAsync<AccountSummary>(projectionTimeout);

        var progress = await context.ProjectionProgressAsync();
        progress.ShouldContain(p => p.ShardName.Contains("AccountSummary", StringComparison.OrdinalIgnoreCase));

        // Read the projection back the Marten way, which is the only place Marten appears here.
        var store = resource.RootServices.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession();
        var summary = await session.LoadAsync<AccountSummary>(id);
        summary.ShouldNotBeNull();
        summary.Deposits.ShouldBe(2);
        summary.Balance.ShouldBe(25m);
    }

    [PostgresFact]
    public async Task a_projection_name_matching_no_shard_is_a_wiring_error_not_a_pass()
    {
        await cleanSchema();
        await using var resource = hostResource();
        await resource.Start();
        var context = contextFor(resource);
        var id = Guid.NewGuid();
        await context.ExecuteAggregateCommandAsync<Account>(new OpenAccount(id, "Cy"), id);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => context.WaitForProjectionAsync<Account>(projectionName: "NoSuchProjection", timeout: TimeSpan.FromSeconds(2)));

        ex.Message.ShouldContain("NoSuchProjection");
        ex.Message.ShouldContain("AccountSummary");
    }

    [PostgresFact]
    public async Task reset_empties_the_event_store_through_the_abstraction()
    {
        await cleanSchema();
        await using var resource = hostResource();
        await resource.Start();
        var context = contextFor(resource);
        var id = Guid.NewGuid();
        await context.ExecuteAggregateCommandAsync<Account>(new OpenAccount(id, "Dee"), id);
        (await context.FetchEventStreamAsync(id)).ShouldNotBeEmpty();

        await context.ResetCritterStackAsync();

        (await context.FetchEventStreamAsync(id)).ShouldBeEmpty();
        (await context.AggregateEventStreamAsync<Account>(id)).ShouldBeNull();
    }

    [PostgresFact]
    public async Task the_store_is_resolved_as_an_IEventStore_not_a_document_store()
    {
        await using var resource = hostResource();
        await resource.Start();

        var store = contextFor(resource).EventStore();

        store.Identity.Type.ShouldBe("marten");
        // The same instance Marten registered — one store, reached through the abstraction.
        store.ShouldBeSameAs(resource.RootServices.GetRequiredService<IDocumentStore>());
    }

    // --- host ----------------------------------------------------------------------------------

    /// <summary>
    /// Wolverine dispatching into Marten, with Marten's own async daemon projecting
    /// <see cref="AccountSummary"/>. Deliberately no <c>IntegrateWithWolverine</c> — the handlers save
    /// their own session, so the test proves the helpers without WolverineFx.Marten in the picture.
    /// </summary>
    private static HostResource hostResource()
        => new(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddMarten(configureStore).AddAsyncDaemon(DaemonMode.Solo);
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<AccountHandler>();
            });
            return builder.Build();
        });

    private static void configureStore(StoreOptions options)
    {
        options.Connection(PostgresEnvironment.ConnectionString);
        options.DatabaseSchemaName = schema;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        // A self-aggregating snapshot with an async lifecycle, so there is a real daemon shard to wait on.
        options.Projections.Snapshot<AccountSummary>(SnapshotLifecycle.Async);
    }

    /// <summary>
    /// Empties the schema through a standalone store BEFORE the host (and its daemon) starts, so the
    /// daemon's high-water mark begins where the data does rather than wherever the last test left it.
    /// </summary>
    private static async Task cleanSchema()
    {
        await using var store = DocumentStore.For(configureStore);
        await store.Advanced.Clean.DeleteAllDocumentsAsync();
        await store.Advanced.Clean.DeleteAllEventDataAsync();
    }

    private static IStepContext contextFor(IHostResource resource)
    {
        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>(null).Returns(resource);
        context.Cancellation.Returns(CancellationToken.None);
        return context;
    }
}

// --- the sample domain -------------------------------------------------------------------------

public record OpenAccount(Guid AccountId, string Owner);
public record Deposit(Guid AccountId, decimal Amount);

public record AccountOpened(Guid AccountId, string Owner);
public record Deposited(Guid AccountId, decimal Amount);

/// <summary>Live-aggregated on read; never persisted.</summary>
public class Account
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = "";
    public decimal Balance { get; set; }

    public void Apply(AccountOpened e)
    {
        Id = e.AccountId;
        Owner = e.Owner;
    }

    public void Apply(Deposited e) => Balance += e.Amount;
}

/// <summary>The async-projected read model.</summary>
public class AccountSummary
{
    public Guid Id { get; set; }
    public int Deposits { get; set; }
    public decimal Balance { get; set; }

    public static AccountSummary Create(AccountOpened e) => new() { Id = e.AccountId };

    public void Apply(Deposited e)
    {
        Deposits++;
        Balance += e.Amount;
    }
}

public class AccountHandler
{
    // IDocumentStore rather than a scoped IDocumentSession: without WolverineFx.Marten, Wolverine's
    // ServiceLocationPolicy refuses Marten's lambda-registered scoped session, and this test is
    // deliberately proving the helpers with no Wolverine-Marten integration in the picture.
    public static async Task Handle(OpenAccount command, IDocumentStore store)
    {
        await using var session = store.LightweightSession();
        session.Events.StartStream<Account>(command.AccountId, new AccountOpened(command.AccountId, command.Owner));
        await session.SaveChangesAsync();
    }

    public static async Task Handle(Deposit command, IDocumentStore store)
    {
        await using var session = store.LightweightSession();
        session.Events.Append(command.AccountId, new Deposited(command.AccountId, command.Amount));
        await session.SaveChangesAsync();
    }
}
