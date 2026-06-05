using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>
/// Assembles a decision-table result grid from per-row cells produced by generated code.
/// Sibling of <see cref="SetVerificationComparer"/>, but positional (one method call per
/// row) rather than key-based. The typed comparison itself is done inline by the generated
/// code through <c>CellCheck.For&lt;T&gt;</c>; this just wires the cells into the step result
/// and marks pass/fail.
/// </summary>
public static class DecisionTableComparer
{
    public static void Apply(StepResult result, string[] columns, IReadOnlyList<CellResult> cells)
    {
        result.IsSetVerification = true; // reuse the grid rendering path
        result.SetVerificationColumns = columns;
        result.MarkCells(cells as CellResult[] ?? cells.ToArray());

        var anyBad = cells.Any(c =>
            c.Status is ResultStatus.failed or ResultStatus.invalid
                or ResultStatus.error or ResultStatus.missing);

        if (anyBad)
            result.MarkFailed();
        else
            result.MarkSuccess();
    }
}
