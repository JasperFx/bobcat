using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Wolverine;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Wolverine.Tracking;

namespace Bobcat.CritterStack;

/// <summary>
/// The result of an aggregate-command execution: the tracked Wolverine session, the events
/// newly appended to the stream, and the rebuilt aggregate — so step assertions can compare
/// against expected state.
/// </summary>
public record AggregateExecution<T>(
    ITrackedSession Session,
    IReadOnlyList<IEvent> NewEvents,
    T? Aggregate);

/// <summary>
/// The layer above <c>Bobcat.Wolverine</c> for the canonical Critter Stack pattern: tracked-session
/// message dispatch <i>and</i> event-store assertion in the same scenario.
/// </summary>
/// <remarks>
/// <para>
/// <b>Store-agnostic by construction.</b> Every helper here reaches the event store through the
/// <c>JasperFx.Events</c> abstractions — <see cref="IEventStore"/>, <see cref="IEventDatabase"/>,
/// <see cref="IQueryEventStore"/>, <c>IProjectionCoordinator</c> — resolved from the registered
/// <see cref="IHostResource"/>'s container. Marten, Polecat and Fisher all register their store as
/// <see cref="IEventStore"/>, so the same spec code runs against any of them and this package has no
/// reference to <c>Bobcat.Marten</c> or to any store. Decision of record 2026-08-20, issue #103.
/// </para>
/// <para>
/// <c>hostResource</c> names the <see cref="IHostResource"/> when a suite registers more than one;
/// <c>storeName</c> names the store (by <see cref="IEventStore.Identity"/>) when a host registers
/// more than one. Both default to "the only one", which is the common case.
/// </para>
/// </remarks>
public static class CritterStackStepContextExtensions
{
    /// <summary>The event store the scenario's host registers — Marten, Polecat or Fisher alike.</summary>
    public static IEventStore EventStore(this IStepContext context, string? hostResource = null, string? storeName = null)
        => context.GetResource<IHostResource>(hostResource).RootServices.EventStore(storeName);

    // --- Reading the store ----------------------------------------------------------------------

    /// <summary>All events in a stream, through the store's read-only view.</summary>
    public static Task<IReadOnlyList<IEvent>> FetchEventStreamAsync(
        this IStepContext context, Guid streamId, string? hostResource = null, string? storeName = null)
        => EventStores.FetchStreamAsync(context.EventStore(hostResource, storeName), streamId, context.Cancellation);

    /// <inheritdoc cref="FetchEventStreamAsync(IStepContext, Guid, string?, string?)"/>
    public static Task<IReadOnlyList<IEvent>> FetchEventStreamAsync(
        this IStepContext context, string streamKey, string? hostResource = null, string? storeName = null)
        => EventStores.FetchStreamAsync(context.EventStore(hostResource, storeName), streamKey, context.Cancellation);

    /// <summary>Rebuild a stream's aggregate from its events, the way the application's own read path would.</summary>
    public static Task<T?> AggregateEventStreamAsync<T>(
        this IStepContext context, Guid streamId, string? hostResource = null, string? storeName = null) where T : class
        => EventStores.AggregateStreamAsync<T>(context.EventStore(hostResource, storeName), streamId, context.Cancellation);

    /// <inheritdoc cref="AggregateEventStreamAsync{T}(IStepContext, Guid, string?, string?)"/>
    public static Task<T?> AggregateEventStreamAsync<T>(
        this IStepContext context, string streamKey, string? hostResource = null, string? storeName = null) where T : class
        => EventStores.AggregateStreamAsync<T>(context.EventStore(hostResource, storeName), streamKey, context.Cancellation);

    // --- Commands ---------------------------------------------------------------------------------

    /// <summary>
    /// Send a command through Wolverine, wait for the tracked session (and all cascading messages)
    /// to settle, then return the newly appended events and the rebuilt aggregate for the given
    /// stream. The canonical "I sent a command, here's what changed" helper.
    /// </summary>
    public static async Task<AggregateExecution<T>> ExecuteAggregateCommandAsync<T>(
        this IStepContext context,
        object command,
        Guid streamId,
        string? hostResource = null,
        string? storeName = null,
        int timeoutInMilliseconds = 5000) where T : class
    {
        var store = context.EventStore(hostResource, storeName);

        var before = await EventStores.FetchStreamAsync(store, streamId, context.Cancellation);
        var beforeCount = before.Count;

        var session = await context.InvokeMessageAndWaitAsync(command, hostResource, timeoutInMilliseconds);

        var all = await EventStores.FetchStreamAsync(store, streamId, context.Cancellation);
        var aggregate = await EventStores.AggregateStreamAsync<T>(store, streamId, context.Cancellation);

        var newEvents = all.Skip(beforeCount).ToList();
        return new AggregateExecution<T>(session, newEvents, aggregate);
    }

    // --- Projections ------------------------------------------------------------------------------

    /// <summary>
    /// Wait until every asynchronous projection has caught up with the store's high-water mark —
    /// the same wait JasperFx's <c>ProjectionScenario</c> performs after each batch of appends.
    /// Returns at once when the store runs no async projections. Spec authors care about the wait,
    /// not how it is implemented; see <see cref="EventStores.WaitForNonStaleProjectionsAsync"/>.
    /// </summary>
    public static Task WaitForNonStaleProjectionsAsync(
        this IStepContext context,
        TimeSpan? timeout = null,
        string? hostResource = null,
        string? storeName = null)
    {
        var services = context.GetResource<IHostResource>(hostResource).RootServices;
        return EventStores.WaitForNonStaleProjectionsAsync(
            services.EventStore(storeName), timeout, services.ProjectionCoordinator(), context.Cancellation);
    }

    /// <summary>
    /// Wait until the async projection shards for <typeparamref name="T"/> (matched by shard name;
    /// override with <paramref name="projectionName"/>) have reached the store's current highest
    /// event sequence. Returns at once when the store runs no async projections at all.
    /// </summary>
    public static Task WaitForProjectionAsync<T>(
        this IStepContext context,
        TimeSpan? timeout = null,
        string? projectionName = null,
        string? hostResource = null,
        string? storeName = null)
        => EventStores.WaitForProjectionAsync<T>(
            context.EventStore(hostResource, storeName), timeout, projectionName, context.Cancellation);

    /// <summary>
    /// Wait until the async projection shards for <typeparamref name="T"/> (matched by shard name;
    /// override with <paramref name="projectionName"/>) have processed at least event sequence
    /// <paramref name="minSequence"/>.
    /// </summary>
    public static Task WaitForProjectionAsync<T>(
        this IStepContext context,
        long minSequence,
        TimeSpan? timeout = null,
        string? projectionName = null,
        string? hostResource = null,
        string? storeName = null)
        => EventStores.WaitForProjectionAsync<T>(
            context.EventStore(hostResource, storeName), minSequence, timeout, projectionName, context.Cancellation);

    /// <summary>The current progress of every async projection shard on the store.</summary>
    public static Task<IReadOnlyList<ShardState>> ProjectionProgressAsync(
        this IStepContext context, string? hostResource = null, string? storeName = null)
        => EventStores.ProjectionProgressAsync(context.EventStore(hostResource, storeName), context.Cancellation);

    // --- Reset --------------------------------------------------------------------------------------

    /// <summary>
    /// Composite between-scenario reset: deletes every event and every document in every event
    /// store the host registers, keeping the schema. With the tracked-session dispatch model,
    /// commands are awaited to completion, so there are typically no in-flight Wolverine envelopes
    /// left to drain; <see cref="CritterStackHostExtensions.ClearStatefulResourcesAsync(IServiceProvider, CancellationToken)"/>
    /// is the JasperFx-native way to purge durable envelope storage and transports when a suite needs it.
    /// </summary>
    public static Task ResetCritterStackAsync(this IStepContext context, string? hostResource = null)
        => context.GetResource<IHostResource>(hostResource).RootServices.ResetEventStoresAsync(context.Cancellation);
}
