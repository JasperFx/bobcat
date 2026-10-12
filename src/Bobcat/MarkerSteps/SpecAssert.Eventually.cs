using System.Diagnostics;
using Bobcat.Engine;
using Bobcat.Engine.Verification;
using Bobcat.Runtime;

namespace Bobcat;

/// <summary>
/// The polling checks -- the projected lane's twin of <c>[WaitFor]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a helper and not the attribute.</b> <c>[WaitFor]</c> is read by the Gherkin generator,
/// which wraps the step it binds in a poll loop. A C# call site has no generated wrapper around the
/// helper's body, so the attribute on a projected step was ignored: the helper ran once and reported
/// its first answer, which looked exactly like support. The generator now warns about that
/// (BOBCAT035), and these are what the warning points at.
/// </para>
/// <para>
/// <b>One verdict, not one per attempt.</b> Every attempt is judged with the same comparison
/// <see cref="Check{T}(string,T,T,CheckOptions?,int)"/> makes, but nothing is recorded until the
/// poll is over. Then the step gets ONE cell: the passing comparison with a
/// <c>converged after</c> note, or the last disagreement with a <c>timed out after</c> note, so a
/// reader sees the value the system was actually stuck on.
/// </para>
/// <para>
/// <b>An exception is "not ready yet"</b> for the length of the window, as it is under
/// <c>[WaitFor]</c>, and the last one is named in the timeout note. Cancellation, and the two
/// exceptions that already mean "stop" in Bobcat (<see cref="SpecCriticalException"/> and
/// <see cref="SpecCatastrophicException"/>), are never swallowed.
/// </para>
/// <para>
/// Silent outside a scenario like everything else here: the poll still runs and the answer still
/// comes back, it just isn't recorded anywhere.
/// </para>
/// </remarks>
public static partial class SpecAssert
{
    /// <summary>How long a poll waits when no timeout is given.</summary>
    public static readonly TimeSpan DefaultEventuallyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often a poll asks when no interval is given. <c>[WaitFor]</c>'s default too.</summary>
    public static readonly TimeSpan DefaultEventuallyInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The name of the cell a polled fact or action records.</summary>
    public const string EventuallyCell = "eventually";

    /// <summary>
    /// Ask for <paramref name="actual"/> until it matches <paramref name="expected"/> or
    /// <paramref name="timeout"/> runs out, and record one cell for the whole poll.
    /// </summary>
    /// <param name="name">The cell's name, as for <see cref="Check{T}(string,T,T,CheckOptions?,int)"/>.</param>
    /// <param name="actual">Reads the current value. A throw counts as "not ready yet".</param>
    /// <param name="expected">The value it should settle on.</param>
    /// <param name="timeout">How long to keep asking. Defaults to <see cref="DefaultEventuallyTimeout"/>.</param>
    /// <param name="interval">How long to wait between asks. Defaults to <see cref="DefaultEventuallyInterval"/>.</param>
    /// <param name="options">The comparison options, a tolerance among them.</param>
    /// <param name="rowIndex">The table row this cell belongs to, or <c>-1</c> for the step's own.</param>
    /// <param name="cancellation">Stops the poll. Never recorded as a verdict.</param>
    /// <returns>Whether the value converged in time.</returns>
    public static Task<bool> EventuallyAsync<T>(string name, Func<T> actual, T expected,
        TimeSpan? timeout = null, TimeSpan? interval = null, CheckOptions? options = null,
        int rowIndex = -1, CancellationToken cancellation = default)
        => EventuallyAsync(name, () => Task.FromResult(actual()), expected, timeout, interval, options,
            rowIndex, cancellation);

    /// <summary>
    /// The asynchronous form of <see cref="EventuallyAsync{T}(string,Func{T},T,TimeSpan?,TimeSpan?,CheckOptions?,int,CancellationToken)"/>,
    /// for a value that has to be fetched -- a document load, a query, an HTTP call.
    /// </summary>
    public static async Task<bool> EventuallyAsync<T>(string name, Func<Task<T>> actual, T expected,
        TimeSpan? timeout = null, TimeSpan? interval = null, CheckOptions? options = null,
        int rowIndex = -1, CancellationToken cancellation = default)
    {
        var expectedText = CheckFormat.Of(expected);

        var outcome = await poll(async () =>
        {
            var cell = CellCheck.For(name, await actual(), expectedText, options, rowIndex);
            return (isGood(cell), cell);
        }, timeout, interval, cancellation);

        var last = outcome.Cell ?? new CellResult(name, ResultStatus.failed)
        {
            Expected = expectedText,
            RowIndex = rowIndex
        };

        return record(last.WithNote(outcome.Note));
    }

    /// <summary>
    /// Ask <paramref name="condition"/> until it answers true or <paramref name="timeout"/> runs out --
    /// a <see cref="Fact"/> that is allowed to take a while.
    /// </summary>
    /// <param name="because">Context for the failure, shown ahead of the timeout note.</param>
    public static Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan? timeout = null,
        TimeSpan? interval = null, string? because = null, CancellationToken cancellation = default)
        => EventuallyAsync(() => Task.FromResult(condition()), timeout, interval, because, cancellation);

    /// <summary>The asynchronous form of <see cref="EventuallyAsync(Func{bool},TimeSpan?,TimeSpan?,string?,CancellationToken)"/>.</summary>
    public static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, TimeSpan? timeout = null,
        TimeSpan? interval = null, string? because = null, CancellationToken cancellation = default)
    {
        var outcome = await poll(async () => (await condition(), null), timeout, interval, cancellation);
        return record(verdictCell(outcome, because));
    }

    /// <summary>
    /// Run <paramref name="action"/> until it stops throwing or <paramref name="timeout"/> runs out --
    /// a <c>[WaitFor]</c> on a void step.
    /// </summary>
    public static Task<bool> EventuallyAsync(Action action, TimeSpan? timeout = null,
        TimeSpan? interval = null, string? because = null, CancellationToken cancellation = default)
        => EventuallyAsync(() =>
        {
            action();
            return Task.CompletedTask;
        }, timeout, interval, because, cancellation);

    /// <summary>The asynchronous form of <see cref="EventuallyAsync(Action,TimeSpan?,TimeSpan?,string?,CancellationToken)"/>.</summary>
    public static async Task<bool> EventuallyAsync(Func<Task> action, TimeSpan? timeout = null,
        TimeSpan? interval = null, string? because = null, CancellationToken cancellation = default)
    {
        var outcome = await poll(async () =>
        {
            await action();
            return (true, null);
        }, timeout, interval, cancellation);

        return record(verdictCell(outcome, because));
    }

    private readonly record struct PollOutcome(bool Converged, CellResult? Cell, string Note);

    private static bool isGood(CellResult cell) => cell.Status is ResultStatus.success or ResultStatus.ok;

    private static CellResult verdictCell(PollOutcome outcome, string? because)
    {
        var note = outcome.Converged || string.IsNullOrEmpty(because)
            ? outcome.Note
            : $"{because}; {outcome.Note}";

        return new CellResult(EventuallyCell, outcome.Converged ? ResultStatus.success : ResultStatus.failed)
        {
            Note = note
        };
    }

    /// <summary>
    /// The loop itself: attempt at once, then every <paramref name="interval"/> until the attempt
    /// says it's done or the time is up. Records nothing; the caller records the one cell.
    /// </summary>
    private static async Task<PollOutcome> poll(Func<Task<(bool Done, CellResult? Cell)>> attempt,
        TimeSpan? timeout, TimeSpan? interval, CancellationToken cancellation)
    {
        var timeoutMs = (long)(timeout ?? DefaultEventuallyTimeout).TotalMilliseconds;
        var intervalMs = (long)(interval ?? DefaultEventuallyInterval).TotalMilliseconds;

        var clock = Stopwatch.StartNew();
        var attempts = 0;
        CellResult? last = null;
        Exception? lastException = null;

        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            attempts++;

            try
            {
                var (done, cell) = await attempt();
                last = cell ?? last;
                lastException = null;

                if (done)
                {
                    return new PollOutcome(true, cell,
                        $"converged after {clock.ElapsedMilliseconds}ms ({attempts} polls)");
                }
            }
            catch (Exception e) when (DecisionTableComparer.IsRowFailure(e))
            {
                // Not ready yet. The same three exceptions a table row refuses to absorb are let
                // through here, for the same reason: they mean stop, not "try again".
                lastException = e;
            }

            if (clock.ElapsedMilliseconds >= timeoutMs)
            {
                var note = $"timed out after {timeoutMs}ms @{intervalMs}ms ({attempts} attempts)";
                if (lastException is not null) note += $"; last error: {lastException.Message}";

                return new PollOutcome(false, last, note);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(intervalMs), cancellation);
        }
    }
}
