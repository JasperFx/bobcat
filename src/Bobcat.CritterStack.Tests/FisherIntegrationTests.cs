using Bobcat.Engine;
using Bobcat.Runtime;
using Fisher;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// The same store-agnostic helpers <see cref="MartenIntegrationTests"/> proves against Marten, proved
/// against Fisher — a temp SQLite file, no docker, the inner-loop store issue #103 names as the
/// target. Every call goes through <c>JasperFx.Events.IEventStore</c> resolved from the host; the
/// only place <c>Fisher</c> appears is the host setup and one read-back, exactly as Marten does in its
/// twin. Fisher is also the store that takes the <i>other</i> branch of each convention path in
/// <c>EventStores</c>: its read-only view is not an <see cref="IQueryEventStore"/>, so aggregation
/// goes through the <c>IEventStore&lt;,&gt;</c> session closure, and its reset is spelled
/// <c>ResetAllDataAsync</c>. A fake proved those branches before; this proves them on the real thing.
/// </summary>
public class FisherIntegrationTests
{
    private static readonly TimeSpan projectionTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task execute_aggregate_command_returns_the_new_events_and_the_rebuilt_aggregate()
    {
        await using var database = TemporarySqliteDatabase.Create();
        await using var resource = hostResource(database);
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
        // Fisher's read-only view cannot aggregate, so this is the session-closure path, on a real store.
        (await context.AggregateEventStreamAsync<Account>(id))!.Balance.ShouldBe(50m);
    }

    [Fact]
    public async Task waits_for_the_async_projection_to_catch_up()
    {
        await using var database = TemporarySqliteDatabase.Create();
        await using var resource = hostResource(database);
        await resource.Start();
        var context = contextFor(resource);
        var id = Guid.NewGuid();

        await context.ExecuteAggregateCommandAsync<Account>(new OpenAccount(id, "Bea"), id);
        await context.ExecuteAggregateCommandAsync<Account>(new Deposit(id, 10m), id);
        await context.ExecuteAggregateCommandAsync<Account>(new Deposit(id, 15m), id);

        // Both spellings of the wait, both through IEventDatabase — Fisher's hosted daemon is the one
        // doing the work, and nothing here knows or cares.
        await context.WaitForNonStaleProjectionsAsync(projectionTimeout);
        await context.WaitForProjectionAsync<AccountSummary>(projectionTimeout);

        var progress = await context.ProjectionProgressAsync();
        progress.ShouldContain(p => p.ShardName.Contains("AccountSummary", StringComparison.OrdinalIgnoreCase));

        // Read the projection back the Fisher way, which is the only place Fisher appears here.
        var store = resource.RootServices.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession();
        var summary = await session.LoadAsync<AccountSummary>(id);
        summary.ShouldNotBeNull();
        summary.Deposits.ShouldBe(2);
        summary.Balance.ShouldBe(25m);
    }

    [Fact]
    public async Task a_projection_name_matching_no_shard_is_a_wiring_error_not_a_pass()
    {
        await using var database = TemporarySqliteDatabase.Create();
        await using var resource = hostResource(database);
        await resource.Start();
        var context = contextFor(resource);
        var id = Guid.NewGuid();
        await context.ExecuteAggregateCommandAsync<Account>(new OpenAccount(id, "Cy"), id);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => context.WaitForProjectionAsync<Account>(projectionName: "NoSuchProjection", timeout: TimeSpan.FromSeconds(2)));

        ex.Message.ShouldContain("NoSuchProjection");
        ex.Message.ShouldContain("AccountSummary");
    }

    [Fact]
    public async Task reset_empties_the_event_store_through_the_abstraction()
    {
        await using var database = TemporarySqliteDatabase.Create();
        await using var resource = hostResource(database);
        await resource.Start();
        var context = contextFor(resource);
        var id = Guid.NewGuid();
        await context.ExecuteAggregateCommandAsync<Account>(new OpenAccount(id, "Dee"), id);
        (await context.FetchEventStreamAsync(id)).ShouldNotBeEmpty();

        // Fisher spells it Advanced.ResetAllDataAsync — the second spelling the reset convention knows.
        await context.ResetCritterStackAsync();

        (await context.FetchEventStreamAsync(id)).ShouldBeEmpty();
        (await context.AggregateEventStreamAsync<Account>(id)).ShouldBeNull();
    }

    [Fact]
    public async Task the_store_is_resolved_as_an_IEventStore_not_a_document_store()
    {
        await using var database = TemporarySqliteDatabase.Create();
        await using var resource = hostResource(database);
        await resource.Start();

        var store = contextFor(resource).EventStore();

        store.Identity.Type.ShouldBe("fisher");
        // The same instance Fisher registered — one store, reached through the abstraction.
        store.ShouldBeSameAs(resource.RootServices.GetRequiredService<IDocumentStore>());
    }

    // --- host ----------------------------------------------------------------------------------

    /// <summary>
    /// Wolverine dispatching into Fisher, with Fisher's own hosted daemon projecting
    /// <see cref="AccountSummary"/>. Deliberately no <c>IntegrateWithWolverine</c>, for the same reason
    /// as the Marten twin: the handlers save their own session, so the test proves the helpers without
    /// WolverineFx.Fisher in the picture.
    /// </summary>
    private static HostResource hostResource(TemporarySqliteDatabase database)
        => new(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddFisher(options =>
            {
                options.Connection(database.ConnectionString);
                options.AutoCreateSchemaObjects = AutoCreate.All;
                // A self-aggregating snapshot with an async lifecycle, so there is a real daemon shard to wait on.
                options.Projections.Snapshot<AccountSummary>(SnapshotLifecycle.Async);
            })
            // Order matters, and it is a registration order: the daemon is a hosted service that reads
            // fi_event_progression as soon as it starts, and Fisher builds its schema lazily on the first
            // session — so without this, registered FIRST, the host fails to start with "no such table".
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<FisherAccountHandler>();
            });
            return builder.Build();
        });

    private static IStepContext contextFor(IHostResource resource)
    {
        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>(null).Returns(resource);
        context.Cancellation.Returns(CancellationToken.None);
        return context;
    }
}

/// <summary>
/// The Fisher twin of <c>AccountHandler</c>: the same commands and events, saved through Fisher's own
/// store. <c>IDocumentStore</c> rather than a scoped session for the same reason as the Marten one —
/// no WolverineFx.Fisher integration is registered, on purpose.
/// </summary>
public class FisherAccountHandler
{
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

/// <summary>
/// A SQLite database file under the temp directory that deletes itself — Fisher's own
/// <c>Fisher.TestUtils.TemporaryDatabase</c> is not a published package, so this is the same twenty
/// lines. Clearing the pool first matters: Microsoft.Data.Sqlite pools by connection string and a
/// pooled connection keeps the file (and its -wal / -shm sidecars) open.
/// </summary>
public sealed class TemporarySqliteDatabase : IAsyncDisposable
{
    private TemporarySqliteDatabase(string path)
    {
        Path = path;
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    public string Path { get; }
    public string ConnectionString { get; }

    public static TemporarySqliteDatabase Create()
        => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bobcat-critterstack-{Guid.NewGuid():n}.db"));

    public ValueTask DisposeAsync()
    {
        using (var pooled = new SqliteConnection(ConnectionString))
        {
            SqliteConnection.ClearPool(pooled);
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(Path + suffix);
            }
            catch (IOException)
            {
                // A connection elsewhere in the run still holds the file; a stray temp file is not worth failing over.
            }
        }

        return ValueTask.CompletedTask;
    }
}
