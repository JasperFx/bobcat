using Bobcat.Engine;
using JasperFx.Events;
using Wolverine.Tracking;

namespace Bobcat.CritterStack;

/// <summary>
/// The stream a scenario is arranging and acting on — aggregate type plus identity (a
/// <see cref="Guid"/> or a string stream key). <see cref="CritterStackFixture"/>'s Given steps
/// publish it onto the scenario-state blackboard, so a cooperating grammar performing its own act
/// (issue #210's <see cref="HttpGrammars"/>) can bracket the same stream without sharing a fixture
/// field — the issue #212 contract: two grammars agreeing only on a capture type.
/// </summary>
public sealed record ScenarioStream(Type Aggregate, object Identity);

/// <summary>
/// The shared act bracket behind every Critter Stack act step — a dispatched command, a tracked
/// HTTP call, a composed grammar's own act: snapshot the scenario's current stream, run the
/// tracked dispatch, and capture what it did (session, newly appended events, or the exception)
/// as a <see cref="TrackedExecution"/>, published onto the scenario-state blackboard so every
/// assertion step — whichever grammar declares it — reads the same record.
/// </summary>
public static class TrackedActs
{
    /// <summary>
    /// Run one act. A failure is <i>captured</i>, never thrown — the WhenCommand contract, which
    /// is what lets <c>Then validation fails with …</c> assert on it. The stream to bracket comes
    /// from <paramref name="streamIdentity"/> when the caller knows it (the fixture's typed
    /// steps), else from the scenario's published <see cref="ScenarioStream"/>, else no stream is
    /// bracketed and <see cref="TrackedExecution.NewEvents"/> stays empty.
    /// </summary>
    public static async Task<TrackedExecution> ExecuteAsync(
        IStepContext context,
        Func<Task<ITrackedSession>> dispatch,
        object? streamIdentity = null,
        string? hostResource = null,
        string? storeName = null)
    {
        streamIdentity ??= context.TryGetState<ScenarioStream>(out var stream) ? stream.Identity : null;

        var before = await fetchStreamAsync(context, streamIdentity, hostResource, storeName);

        TrackedExecution execution;
        try
        {
            var session = await dispatch();
            var after = await fetchStreamAsync(context, streamIdentity, hostResource, storeName);
            execution = new TrackedExecution(session, after.Skip(before.Count).ToList(), null);

            // Observed run evidence (issue #107): the events the act actually appended and the
            // messages the tracked session actually sent — never what a Then merely names.
            recordTouched(context, execution.NewEvents.Select(e => e.Data));
            recordTouched(context, session.Sent.AllMessages());
        }
        catch (Exception e)
        {
            execution = new TrackedExecution(null, [], e);
        }

        context.SetState(execution);
        return execution;
    }

    private static Task<IReadOnlyList<IEvent>> fetchStreamAsync(
        IStepContext context, object? identity, string? hostResource, string? storeName)
        => identity switch
        {
            string key => context.FetchEventStreamAsync(key, hostResource, storeName),
            Guid id when id != Guid.Empty => context.FetchEventStreamAsync(id, hostResource, storeName),
            _ => Task.FromResult<IReadOnlyList<IEvent>>([]),
        };

    private static void recordTouched(IStepContext context, IEnumerable<object> items)
    {
        foreach (var item in items)
        {
            if (item != null) context.RecordTouchedType(item.GetType());
        }
    }
}
