using JasperFx.Events;
using Wolverine.Tracking;

namespace Bobcat.CritterStack;

/// <summary>
/// What one act — a dispatched command or a tracked HTTP call — did to the system: the tracked
/// Wolverine session (everything the act caused, cascades and local queues included), the events
/// newly appended to the scenario's current stream, and the exception the act raised when it
/// failed. This is the capture every store-grammar <c>Then</c> step reads
/// (<c>Then {event} is emitted</c>, <c>Then {message} is sent</c>, the refusal checks), and it is
/// deliberately a public, typed seam rather than private fixture fields: another grammar acting on
/// the same scenario — issue #210's HTTP steps, a composed module under issue #212 — feeds those
/// assertions by handing its capture to <see cref="CritterStackFixture.RecordExecution"/>, so the
/// assertion vocabulary works unchanged whichever lane performed the act.
/// </summary>
/// <param name="Session">The tracked session, or null when the act threw before one completed.</param>
/// <param name="NewEvents">The events the act appended to the scenario's current stream. Empty when
/// it appended none, failed, or no stream is established.</param>
/// <param name="Error">The exception the act raised, or null when it succeeded — the subject of
/// <c>Then validation fails with …</c>.</param>
public sealed record TrackedExecution(
    ITrackedSession? Session,
    IReadOnlyList<IEvent> NewEvents,
    Exception? Error)
{
    /// <summary>The starting state: nothing has acted yet.</summary>
    public static readonly TrackedExecution None = new(null, [], null);
}
