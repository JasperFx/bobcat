using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// The store-agnostic binding against fakes shaped like the three stores, so the two convention
/// paths (aggregate through a session's <c>Events</c>; reset through <c>Advanced</c>) are proven
/// without referencing Marten, Polecat or Fisher. The Marten leg is
/// <see cref="MartenIntegrationTests"/>.
/// </summary>
public class EventStoresTests
{
    public class Account
    {
        public Guid Id { get; set; }
    }

    /// <summary>A session as the stores shape one: IStorageOperations with an Events member.</summary>
    public interface IFakeSession : IStorageOperations
    {
        IQueryEventStore Events { get; }
    }

    // --- aggregate ----------------------------------------------------------------------------

    [Fact]
    public async Task aggregates_through_the_read_only_view_when_it_is_a_query_event_store()
    {
        // Marten and Polecat: OpenReadOnlyEventStore() returns the session's IQueryEventStore.
        var id = Guid.NewGuid();
        var expected = new Account { Id = id };
        var view = Substitute.For<IQueryEventStore, IReadOnlyEventStore>();
        view.AggregateStreamAsync<Account>(id, token: Arg.Any<CancellationToken>()).Returns(expected);

        var store = new FakeEventStore { ReadOnlyView = (IReadOnlyEventStore)view };

        var aggregate = await EventStores.AggregateStreamAsync<Account>(store, id);

        aggregate.ShouldBeSameAs(expected);
    }

    [Fact]
    public async Task falls_back_to_a_session_opened_through_the_generic_closure()
    {
        // Fisher: the read-only view is a separate object, so a session is opened through
        // IEventStore<TOperations, TQuerySession>.OpenSession and its Events member is used.
        var id = Guid.NewGuid();
        var expected = new Account { Id = id };

        var events = Substitute.For<IQueryEventStore>();
        events.AggregateStreamAsync<Account>(id, token: Arg.Any<CancellationToken>()).Returns(expected);
        var session = Substitute.For<IFakeSession>();
        session.Events.Returns(events);

        var database = Substitute.For<IEventDatabase>();
        var store = Substitute.For<IEventStore<IFakeSession, IFakeSession>>();
        store.AllDatabases().Returns(new ValueTask<IReadOnlyList<IEventDatabase>>([database]));
        store.OpenReadOnlyEventStore().Returns(Substitute.For<IReadOnlyEventStore>());
        store.OpenSession(database).Returns(session);

        var aggregate = await EventStores.AggregateStreamAsync<Account>(store, id);

        aggregate.ShouldBeSameAs(expected);
        await session.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task explains_when_no_session_can_be_opened()
    {
        var store = Substitute.For<IEventStore<IFakeSession, IFakeSession>>();
        store.AllDatabases().Returns(new ValueTask<IReadOnlyList<IEventDatabase>>(Array.Empty<IEventDatabase>()));
        store.OpenReadOnlyEventStore().Returns(Substitute.For<IReadOnlyEventStore>());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => EventStores.AggregateStreamAsync<Account>(store, Guid.NewGuid()));

        ex.Message.ShouldContain("enumerates no databases");
    }

    [Fact]
    public void a_session_without_a_query_event_store_is_reported_not_guessed()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => EventStoreSessions.QueryEventStoreOf(new object()));

        ex.Message.ShouldContain("no public 'Events' member");
    }

    // --- reset convention ---------------------------------------------------------------------

    [Fact]
    public async Task reset_prefers_ResetAllData()
    {
        var store = new StoreWithResetAllData();
        await StoreResetConvention.ResetAsync(store, CancellationToken.None);
        store.Advanced.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task reset_accepts_the_Async_suffixed_spelling()
    {
        // Fisher spells it ResetAllDataAsync.
        var store = new StoreWithResetAllDataAsync();
        await StoreResetConvention.ResetAsync(store, CancellationToken.None);
        store.Advanced.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task reset_falls_back_to_the_cleaner_pair()
    {
        var store = new StoreWithCleanerOnly();
        await StoreResetConvention.ResetAsync(store, CancellationToken.None);
        store.Advanced.Clean.Calls.ShouldBe(["events", "documents"]);
    }

    [Fact]
    public async Task reset_says_what_it_looked_for_when_nothing_matches()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => StoreResetConvention.ResetAsync(new FakeEventStore(), CancellationToken.None));

        ex.Message.ShouldContain("Advanced.ResetAllData(CancellationToken)");
        ex.Message.ShouldContain("DeleteAllEventDataAsync");
        ex.Message.ShouldContain("reset hook");
    }

    // --- projection waits ----------------------------------------------------------------------

    [Fact]
    public async Task no_async_shards_means_nothing_to_wait_for()
    {
        var database = databaseWithProgress([]);
        var store = new FakeEventStore { Databases = [database] };

        await EventStores.WaitForProjectionAsync<Account>(store, minSequence: 10, timeout: TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task a_name_matching_no_shard_fails_loudly_and_lists_the_shards()
    {
        var database = databaseWithProgress([new ShardState("Orders:All", 5), new ShardState("Billing:All", 5)]);
        var store = new FakeEventStore { Databases = [database] };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => EventStores.WaitForProjectionAsync<Account>(store, minSequence: 1, timeout: TimeSpan.FromMilliseconds(200)));

        ex.Message.ShouldContain("matches 'Account'");
        ex.Message.ShouldContain("'Orders:All'");
        ex.Message.ShouldContain("'Billing:All'");
    }

    [Fact]
    public async Task waits_until_the_matching_shards_reach_the_sequence()
    {
        var calls = 0;
        var database = Substitute.For<IEventDatabase>();
        database.AllProjectionProgress(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            IReadOnlyList<ShardState> progress =
            [
                new ShardState(ShardState.HighWaterMark, 10),
                new ShardState("Account:All", calls >= 3 ? 10 : 4),
                new ShardState("Other:All", 1), // never catches up, and must not be waited on
            ];
            return Task.FromResult(progress);
        });
        var store = new FakeEventStore { Databases = [database] };

        await EventStores.WaitForProjectionAsync<Account>(store, minSequence: 10, timeout: TimeSpan.FromSeconds(5));

        calls.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task times_out_with_the_shard_positions()
    {
        var database = databaseWithProgress([new ShardState("Account:All", 2)]);
        var store = new FakeEventStore { Databases = [database] };

        var ex = await Should.ThrowAsync<TimeoutException>(
            () => EventStores.WaitForProjectionAsync<Account>(store, minSequence: 9, timeout: TimeSpan.FromMilliseconds(150)));

        ex.Message.ShouldContain("'Account:All' at 2");
    }

    [Fact]
    public async Task the_target_without_a_sequence_is_the_stores_highest_event()
    {
        var database = databaseWithProgress([new ShardState("Account:All", 7)]);
        database.FetchHighestEventSequenceNumber(Arg.Any<CancellationToken>()).Returns(7L);
        var store = new FakeEventStore { Databases = [database] };

        await EventStores.WaitForProjectionAsync<Account>(store, timeout: TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task non_stale_wait_goes_through_every_database()
    {
        var first = Substitute.For<IEventDatabase>();
        var second = Substitute.For<IEventDatabase>();
        var store = new FakeEventStore { Databases = [first, second] };

        await EventStores.WaitForNonStaleProjectionsAsync(store, TimeSpan.FromSeconds(1));

        await first.Received(1).WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(1));
        await second.Received(1).WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task non_stale_wait_falls_back_to_the_coordinator_then_explains()
    {
        var store = new FakeEventStore();
        var daemon = Substitute.For<IProjectionDaemon>();
        var coordinator = Substitute.For<IProjectionCoordinator>();
        coordinator.DaemonForMainDatabase().Returns(daemon);

        await EventStores.WaitForNonStaleProjectionsAsync(store, TimeSpan.FromSeconds(1), coordinator);
        await daemon.Received(1).WaitForNonStaleData(TimeSpan.FromSeconds(1));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => EventStores.WaitForNonStaleProjectionsAsync(store, TimeSpan.FromSeconds(1)));
        ex.Message.ShouldContain("IProjectionCoordinator");
    }

    private static IEventDatabase databaseWithProgress(IReadOnlyList<ShardState> progress)
    {
        var database = Substitute.For<IEventDatabase>();
        database.Identifier.Returns("main");
        database.AllProjectionProgress(Arg.Any<CancellationToken>()).Returns(Task.FromResult(progress));
        return database;
    }

    // --- fakes shaped like the stores' public surface --------------------------------------------

    private sealed class StoreWithResetAllData : FakeEventStore
    {
        public Ops Advanced { get; } = new();

        public sealed class Ops
        {
            public int Calls { get; private set; }
            public Task ResetAllData(CancellationToken token) { Calls++; return Task.CompletedTask; }
        }
    }

    private sealed class StoreWithResetAllDataAsync : FakeEventStore
    {
        public Ops Advanced { get; } = new();

        public sealed class Ops
        {
            public int Calls { get; private set; }
            public Task ResetAllDataAsync(CancellationToken token = default) { Calls++; return Task.CompletedTask; }
        }
    }

    private sealed class StoreWithCleanerOnly : FakeEventStore
    {
        public Ops Advanced { get; } = new();

        public sealed class Ops
        {
            public Cleaner Clean { get; } = new();
        }

        public sealed class Cleaner
        {
            public List<string> Calls { get; } = [];
            public Task DeleteAllEventDataAsync(CancellationToken token = default) { Calls.Add("events"); return Task.CompletedTask; }
            public Task DeleteAllDocumentsAsync(CancellationToken token = default) { Calls.Add("documents"); return Task.CompletedTask; }
        }
    }
}
