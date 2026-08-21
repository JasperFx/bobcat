namespace Bobcat.Engine;

/// <summary>
/// Interim state reported by a long-running step <em>while</em> it executes, surfaced through
/// <see cref="IExecutionObserver.StepProgress"/> so renderers can update the live step row in
/// place instead of waiting for <see cref="IExecutionObserver.StepFinished"/>.
/// <para>
/// This is the engine seam for live early/partial results: a poll loop can report
/// "waiting… last value 3", and on timeout that last interim value is already on screen.
/// The same model feeds the future HTML renderer and the planned test-projection front-end.
/// </para>
/// </summary>
public class StepUpdate
{
    /// <summary>
    /// Short human-readable status for the live row (e.g. "waiting… last value 3"). May be null.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Partial cell results known so far (e.g. rows already verified in a set comparison).
    /// Empty when the update carries only a status <see cref="Message"/>.
    /// </summary>
    public IReadOnlyList<CellResult> Cells { get; init; } = Array.Empty<CellResult>();

    /// <summary>
    /// 1-based index of the table row the step is working on, when the step is a
    /// <c>[TableGrammar]</c> (or anything else row-shaped). Null for an update that is not about
    /// rows. A 200-row grammar is one step to the executor; this is what lets a watcher see
    /// which of the 200 it is on instead of a frozen step line.
    /// </summary>
    public int? Row { get; init; }

    /// <summary>Total rows the step will work through; set together with <see cref="Row"/>.</summary>
    public int? TotalRows { get; init; }

    public StepUpdate()
    {
    }

    public StepUpdate(string message)
    {
        Message = message;
    }

    /// <summary>
    /// Row-only progress: "now on row <paramref name="row"/> of <paramref name="totalRows"/>".
    /// Carries no message on purpose — a console renderer that prints every message would
    /// emit one line per row, so message-less row ticks stay silent there and drive only the
    /// renderers that show a live counter.
    /// </summary>
    public static StepUpdate ForRow(int row, int totalRows) => new() { Row = row, TotalRows = totalRows };
}
