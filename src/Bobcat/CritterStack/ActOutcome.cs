using JasperFx.Events;

namespace Bobcat.CritterStack;

/// <summary>
/// What an act did, in the only terms the store grammar needs to read: the messages it caused to
/// be sent.
/// </summary>
/// <remarks>
/// <para>
/// This interface is why the Critter Stack grammar lives in Bobcat core rather than in a
/// Wolverine-coupled package. When the grammar was deleted, 895 lines of it were removed as
/// "Wolverine-coupled" — and exactly <b>11</b> of those lines mentioned a Wolverine type. Every
/// one of them was reading two things off a tracked session: the messages it sent, typed or
/// untyped. That is this interface.
/// </para>
/// <para>
/// So arranging events, asserting what was emitted, and asserting a read model are store work,
/// available to a Marten, Polecat or Fisher application with no message bus anywhere near it.
/// Only the <i>act</i> needs one, and it arrives through
/// <see cref="CritterStackFixture.DispatchAsync"/>.
/// </para>
/// </remarks>
public interface IActOutcome
{
    /// <summary>Every message the act caused to be sent, cascades included.</summary>
    IReadOnlyList<object> MessagesSent { get; }
}

/// <summary>
/// What one act did to the system: its <see cref="Outcome"/>, the events it appended to the
/// scenario's current stream, and the exception it raised when it failed.
/// </summary>
/// <remarks>
/// Deliberately a public, typed seam rather than private fixture fields: another grammar acting on
/// the same scenario feeds the same assertions by handing its capture to
/// <see cref="CritterStackFixture.RecordExecution"/>, so the assertion vocabulary works unchanged
/// whichever lane performed the act.
/// </remarks>
/// <param name="Outcome">The act's outcome, or null when it threw before completing.</param>
/// <param name="NewEvents">The events the act appended to the scenario's current stream. Empty when
/// it appended none, failed, or no stream is established.</param>
/// <param name="Error">The exception the act raised, or null when it succeeded — the subject of
/// <c>Then validation fails with …</c>.</param>
public sealed record ActExecution(
    IActOutcome? Outcome,
    IReadOnlyList<IEvent> NewEvents,
    Exception? Error)
{
    /// <summary>The starting state: nothing has acted yet.</summary>
    public static readonly ActExecution None = new(null, [], null);
}

/// <summary>
/// What a command did: the act's outcome, the events it appended, and the aggregate rebuilt from
/// the stream afterwards.
/// </summary>
public sealed record AggregateExecution<T>(
    IActOutcome Outcome,
    IReadOnlyList<IEvent> NewEvents,
    T? Aggregate);
