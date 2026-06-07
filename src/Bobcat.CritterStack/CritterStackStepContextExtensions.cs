using System.Diagnostics;
using Bobcat.Engine;
using Bobcat.Marten;
using Bobcat.Runtime;
using Bobcat.Wolverine;
using JasperFx.Events;
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
/// The layer above <c>Bobcat.Wolverine</c> and <c>Bobcat.Marten</c> for the canonical Critter
/// Stack pattern: tracked-session message dispatch <i>and</i> event-store assertion in the same
/// scenario. These helpers wire the two per-package extension sets together rather than
/// replacing either.
/// </summary>
public static class CritterStackStepContextExtensions
{
    /// <summary>
    /// Send a command through Wolverine, wait for the tracked session (and all cascading
    /// messages) to settle, then return the newly appended events and the rebuilt aggregate
    /// for the given stream. The canonical "I sent a command, here's what changed" helper.
    /// </summary>
    public static async Task<AggregateExecution<T>> ExecuteAggregateCommandAsync<T>(
        this IStepContext context,
        object command,
        Guid streamId,
        string? wolverineResource = null,
        string? martenResource = null) where T : class
    {
        var before = await context.FetchStreamAsync(streamId, martenResource);
        var beforeCount = before.Count;

        var session = await context.InvokeMessageAndWaitAsync(command, wolverineResource);

        var all = await context.FetchStreamAsync(streamId, martenResource);
        var aggregate = await context.AggregateStreamAsync<T>(streamId, martenResource);

        var newEvents = all.Skip(beforeCount).ToList();
        return new AggregateExecution<T>(session, newEvents, aggregate);
    }

    /// <summary>
    /// Wait for the async projection daemon to catch up to at least <paramref name="minSequence"/>
    /// for the projection of type <typeparamref name="T"/> (matched by shard name), polling with
    /// exponential backoff. Spec authors care about the wait, not how it's implemented.
    /// </summary>
    public static async Task WaitForProjectionAsync<T>(
        this IStepContext context,
        Guid streamId,
        long minSequence,
        TimeSpan? timeout = null,
        string? martenResource = null)
    {
        var store = context.GetResource<IMartenResource>(martenResource).DocumentStore;
        var deadline = timeout ?? TimeSpan.FromSeconds(5);
        var interval = TimeSpan.FromMilliseconds(50);
        var maxInterval = TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();

        while (true)
        {
            var progress = await store.Advanced.AllProjectionProgress(token: context.Cancellation);

            var relevant = progress
                .Where(p => p.ShardName.Contains(typeof(T).Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var toCheck = relevant.Count > 0 ? relevant : progress;

            if (toCheck.Count == 0 || toCheck.All(p => p.Sequence >= minSequence))
                return;

            if (sw.Elapsed >= deadline)
                throw new TimeoutException(
                    $"Projection '{typeof(T).Name}' did not reach sequence {minSequence} within {deadline.TotalMilliseconds}ms.");

            await Task.Delay(interval, context.Cancellation);
            interval = TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds * 2, maxInterval.TotalMilliseconds));
        }
    }

    /// <summary>
    /// Composite between-scenario reset: cleans all Marten document + event data. With the
    /// tracked-session dispatch model, commands are awaited to completion, so there are
    /// typically no in-flight Wolverine envelopes left to drain; durable-envelope draining can
    /// be added when a sample requires it.
    /// </summary>
    public static async Task ResetCritterStackAsync(this IStepContext context)
    {
        await context.CleanAllMartenDataAsync();
    }
}
