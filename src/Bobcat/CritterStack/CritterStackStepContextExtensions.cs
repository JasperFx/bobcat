using Bobcat.Engine;
using Bobcat.Runtime;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Bobcat.CritterStack;

/// <summary>
/// Reaching the event store from inside a step: which store, which stream, which projection —
/// bound to the JasperFx.Events abstractions, so the same step code runs on Marten, Polecat or
/// Fisher.
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

    /// <summary>Every event in the store at or after <paramref name="sequenceFloor"/> (issue #319).</summary>
    public static Task<IReadOnlyList<IEvent>> QueryEventsSinceAsync(
        this IStepContext context, long sequenceFloor, string? hostResource = null, string? storeName = null)
        => EventStores.QueryEventsSinceAsync(context.EventStore(hostResource, storeName), sequenceFloor, context.Cancellation);

    /// <summary>The highest sequence the store has issued, or 0 when it is empty (issue #319).</summary>
    public static Task<long> HighWaterSequenceAsync(
        this IStepContext context, string? hostResource = null, string? storeName = null)
        => EventStores.HighWaterSequenceAsync(context.EventStore(hostResource, storeName), context.Cancellation);

    /// <summary>
    /// <see cref="HighWaterSequenceAsync"/> when the host has an event store, else null.
    /// </summary>
    /// <remarks>
    /// Plenty of suites have no event store at all — the message-only and HTTP lanes never touch
    /// one, and asking for it throws by design. So the #319 floor has to be optional: null here
    /// means "nothing to measure against", and the act's appended-event list stays empty exactly
    /// as it did before.
    /// </remarks>
    public static async Task<long?> TryHighWaterSequenceAsync(
        this IStepContext context, string? hostResource = null, string? storeName = null)
    {
        var services = context.GetResource<IHostResource>(hostResource).RootServices;
        if (services.EventStores().Count == 0) return null;

        return await context.HighWaterSequenceAsync(hostResource, storeName).ConfigureAwait(false);
    }

    /// <summary>Rebuild a stream's aggregate from its events, the way the application's own read path would.</summary>
    public static Task<T?> AggregateEventStreamAsync<T>(
        this IStepContext context, Guid streamId, string? hostResource = null, string? storeName = null) where T : class
        => EventStores.AggregateStreamAsync<T>(context.EventStore(hostResource, storeName), streamId, context.Cancellation);

    /// <inheritdoc cref="AggregateEventStreamAsync{T}(IStepContext, Guid, string?, string?)"/>
    public static Task<T?> AggregateEventStreamAsync<T>(
        this IStepContext context, string streamKey, string? hostResource = null, string? storeName = null) where T : class
        => EventStores.AggregateStreamAsync<T>(context.EventStore(hostResource, storeName), streamKey, context.Cancellation);

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
    /// store the host registers, keeping the schema. This purges <em>stores</em> only — for
    /// durable envelope storage and transports, see
    /// <see cref="CritterStackHostExtensions.ClearStatefulResourcesAsync(IServiceProvider, CancellationToken)"/>.
    /// </summary>
    public static Task ResetCritterStackAsync(this IStepContext context, string? hostResource = null)
        => context.GetResource<IHostResource>(hostResource).RootServices.ResetEventStoresAsync(context.Cancellation);
}
