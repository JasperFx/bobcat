using Bobcat.Engine.Verification;

namespace Bobcat.Engine;

/// <summary>
/// When a run writes a report out (issue #408).
/// </summary>
public enum ReportVisibility
{
    /// <summary>
    /// Only for a scenario that failed. The default, and the reason this feature costs a green
    /// suite nothing: a grammar produces its report whether or not anybody wanted it, and a
    /// thousand-scenario run publishing a few hundred rows per scenario is real volume on every
    /// surface at once.
    /// </summary>
    OnFailure,

    /// <summary>
    /// Always, failure or not — for the report whose whole point is the passing case. Still
    /// subject to the row cap.
    /// </summary>
    Always
}

/// <summary>
/// An account of the whole scenario, as a table of cells — Storyteller's custom logging
/// (<c>ISpecContext.Reporting</c>) answered with Bobcat's Cell model instead of its HTML
/// (issue #408).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scenario-scoped, which is the entire point.</b> Everything else a step can say attaches to
/// the step — <see cref="IStepContext.Log"/>, <see cref="IStepContext.AttachDiagnostic"/> and
/// <see cref="IStepContext.RecordCells"/> all route through the executing step and are no-ops
/// between steps. The evidence that actually diagnoses an asynchronous failure does not belong to
/// one step: a tracked messaging session's record spans the scenario and is most valuable *after*
/// the <c>Then</c> that disagreed.
/// </para>
/// <para>
/// <b>Why it is not on the scenario blackboard.</b> <see cref="IStepContext.SetState{T}"/> is
/// <c>ReporterFor&lt;T&gt;()</c> minus the rendering, and reaching for it is the obvious move.
/// Issue #107 already settled it the other way: run evidence deliberately does not ride the
/// blackboard, because <see cref="ExecutionResults.TouchedTypes"/> has to reach the wire. A report
/// has to reach the wire for exactly the same reason, so it accumulates onto the results beside
/// <c>TouchedTypes</c> and <see cref="ExecutionResults.Timeline"/>.
/// </para>
/// <para>
/// <b>Rows are unjudged cells, so a report cannot claim it checked anything.</b> Every cell a
/// <see cref="TableReport"/> builds carries only its text — <see cref="CellResult.Expected"/> and
/// <see cref="CellResult.Actual"/> both null — which is the <i>value</i> shape from issue #396 and
/// travels as <c>StepCell.Value</c>. A report that genuinely wants to judge one row (the envelope
/// that was dead-lettered, among a dozen that were fine) builds an ordinary judged
/// <see cref="CellResult"/> for it, so a red row inside an informational table needs no second
/// mechanism.
/// </para>
/// <para>
/// <b>No declarative twin.</b> #395 settled the rule: the declarative form is canonical where it
/// carries settings a compile-time reader needs — <c>KeyColumns</c>, <c>Ordered</c>,
/// <c>Column</c>. A report carries none, because its columns come from its rows and its rows come
/// from running code, so an attribute would carry no information.
/// </para>
/// </remarks>
public interface IScenarioReport
{
    /// <summary>The heading the report renders under.</summary>
    string Title { get; }

    /// <summary>A shorter heading for a surface with no room for <see cref="Title"/>, or null.</summary>
    string? ShortTitle { get; }

    /// <summary>When this report is written out. See <see cref="ReportVisibility"/>.</summary>
    ReportVisibility Visibility { get; }

    /// <summary>
    /// The column order. The one thing cells cannot carry themselves, so it rides alongside and a
    /// reader reassembles the grid from <see cref="CellResult.RowIndex"/> — the same arrangement
    /// <c>StepFinished.Columns</c> already uses for a set verification.
    /// </summary>
    IReadOnlyList<string> Columns { get; }

    /// <summary>The cells, each stamped with its row.</summary>
    IReadOnlyList<CellResult> Cells { get; }

    /// <summary>
    /// Rows past the cap that were not kept. Renderers say "…and N more", never truncate silently
    /// — the same discipline as the 20-line stderr tail a worker fault carries.
    /// </summary>
    int SuppressedRows { get; }
}

/// <summary>
/// The report shape nearly every producer wants: append rows, let the columns and the row indexes
/// look after themselves, and stop growing at the cap.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cap is not a nicety.</b> <c>docs/tutorials/agent-friendly-tests.md</c> records what
/// happens without one: a suite dumped a 698MB tracked-session log into test output, faulted the
/// worker, and turned every unreported test indeterminate — a run that looked like a crash because
/// something tried to say too much. "Write out more context" needs its boundary stated in the same
/// breath, so the boundary is in the base class rather than left to each producer to remember.
/// </para>
/// </remarks>
public abstract class TableReport : IScenarioReport
{
    /// <summary>Rows kept, by default, before <see cref="SuppressedRows"/> starts counting.</summary>
    public const int DefaultMaxRows = 200;

    private readonly List<CellResult> _cells = new();
    private readonly List<string> _columns = new();
    private int _rows;
    private int _suppressed;

    public abstract string Title { get; }

    public virtual string? ShortTitle => null;

    public virtual ReportVisibility Visibility => ReportVisibility.OnFailure;

    /// <summary>Override to raise or lower the cap for one report.</summary>
    public virtual int MaxRows => DefaultMaxRows;

    public IReadOnlyList<string> Columns => _columns;

    public IReadOnlyList<CellResult> Cells => _cells;

    public int SuppressedRows => _suppressed;

    /// <summary>Rows kept so far.</summary>
    public int RowCount => _rows;

    /// <summary>
    /// Append a row of plain values. Each becomes an <i>unjudged</i> cell, so the row states what
    /// happened without claiming anything was checked.
    /// </summary>
    /// <remarks>
    /// Values are formatted by the same <c>CheckFormat</c> every other cell in Bobcat uses, so a
    /// timestamp in a report and a timestamp in a set verification read identically — ISO, not the
    /// invariant culture's short date.
    /// </remarks>
    protected void Row(params (string Column, object? Value)[] cells)
        => Row(cells.Select(c =>
            new CellResult(c.Column, ResultStatus.ok, CheckFormat.Of(c.Value))).ToList());

    /// <summary>
    /// Append a row of cells built by the caller — the escape hatch for a row that judges
    /// something. <see cref="CellResult.RowIndex"/> is stamped here, so a producer never has to
    /// track it.
    /// </summary>
    protected void Row(IReadOnlyList<CellResult> cells)
    {
        if (_rows >= MaxRows)
        {
            _suppressed++;
            return;
        }

        var row = _rows++;

        foreach (var cell in cells)
        {
            if (!_columns.Contains(cell.Name)) _columns.Add(cell.Name);

            // Restamped rather than asked for: a producer has no reason to know its own row
            // number. Through CellResult.WithRowIndex, which is the only copy that keeps a plain
            // value cell's text — see the note on CellResult.copy.
            _cells.Add(cell.WithRowIndex(row));
        }
    }
}
