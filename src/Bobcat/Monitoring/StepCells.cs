using Bobcat.Engine;

namespace Bobcat.Monitoring;

/// <summary>
/// The one place a <see cref="CellResult"/> becomes a <see cref="StepCell"/> (issue #396).
/// </summary>
/// <remarks>
/// <para>
/// There were two copies of this projection — one in <c>ScenarioRecorder</c> for the projected
/// lane, one in <c>MonitorPublishingObserver</c> for the Gherkin lane — and both dropped a cell's
/// plain text, because both sent <c>Expected</c>/<c>Actual</c>/<c>Note</c> and neither had
/// anywhere to put <see cref="CellResult.DisplayText"/>. One projection cannot do that to one
/// lane and not the other, which is the whole reason it is a single function now.
/// </para>
/// <para>
/// <b>The rule: a cell says exactly one thing.</b> A judged cell carries its pair, a noted cell
/// carries its note, and a cell carrying neither is an <i>unjudged value</i> — a decision table's
/// input column — which travels as <see cref="StepCell.Value"/>. Never both, so a reader is never
/// choosing between two fields that mean the same thing.
/// </para>
/// </remarks>
public static class StepCells
{
    public static StepCell From(CellResult cell)
    {
        // "Nothing was judged, and nothing to remark on" — so the cell's own text IS its content.
        // Deliberately not written into Actual: a cell with no expected and no actual is how the
        // JSON report decides an input column earns no Status column (issue #384), so borrowing
        // Actual for a value would make every input cell look judged.
        var plain = cell.Expected is null && cell.Actual is null && cell.Note is null
            ? cell.DisplayText
            : null;

        return new StepCell(
            cell.Name,
            cell.Status.ToString(),
            cell.Expected,
            cell.Actual,
            cell.Note,
            cell.RowIndex,
            // Empty is not a value. A cell genuinely holding "" and a cell holding nothing are the
            // same to a reader, and null is the honest one of the two.
            string.IsNullOrEmpty(plain) ? null : plain);
    }
}
