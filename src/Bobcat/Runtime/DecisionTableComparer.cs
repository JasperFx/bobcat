using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>
/// The row-level failure rules a decision table (<see cref="TableRunner"/>, one method call per
/// row) shares with <see cref="SetVerificationComparer"/>.
/// </summary>
public static class DecisionTableComparer
{
    /// <summary>
    /// The cell name that carries a row's own failure — the exception a row's call threw, rather
    /// than a disagreement about a value.
    /// </summary>
    /// <remarks>
    /// One of the four row-level markers a grid can carry, beside <c>missing-row</c>,
    /// <c>extra-row</c> and <c>out-of-order</c>. A viewer reading cells generically shows the row's
    /// other cells in place and takes this one as the row's verdict and its explanation.
    /// </remarks>
    public const string RowErrorCell = "row-error";

    /// <summary>
    /// Whether an exception from one table row is that row's failure, leaving the rest of the table
    /// to run, or something the row has no business absorbing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row of a table is an independent case, not a step in a sequence, so an ordinary exception
    /// is the row's own verdict — Storyteller's behaviour, and what lets a table of twenty rows
    /// report the three that broke instead of stopping at the first.
    /// </para>
    /// <para>
    /// The exceptions are the ones that already mean something in Bobcat: a
    /// <see cref="SpecCriticalException"/> says abort this scenario and a
    /// <see cref="SpecCatastrophicException"/> says stop the suite, so a fixture that means "stop
    /// here" has a way to say it; and cancellation is never a verdict about anything.
    /// </para>
    /// </remarks>
    public static bool IsRowFailure(Exception ex)
        => ex is not (SpecCriticalException or SpecCatastrophicException or OperationCanceledException);

    /// <summary>
    /// The <see cref="RowErrorCell"/> cell for a row whose call threw.
    /// </summary>
    public static CellResult RowError(int rowIndex, Exception ex)
        => new(RowErrorCell, ResultStatus.error, $"{ex.GetType().Name}: {ex.Message}")
        {
            RowIndex = rowIndex
        };
}
