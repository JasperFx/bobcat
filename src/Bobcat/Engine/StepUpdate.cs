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

    public StepUpdate()
    {
    }

    public StepUpdate(string message)
    {
        Message = message;
    }
}
