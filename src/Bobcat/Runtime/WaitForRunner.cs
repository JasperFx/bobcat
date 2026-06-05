using System.Diagnostics;
using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>
/// The outcome of a single <see cref="WaitForRunner"/> poll attempt.
/// </summary>
public readonly struct WaitAttempt
{
    public WaitAttempt(bool success, CellResult[] cells)
    {
        Success = success;
        Cells = cells;
    }

    /// <summary>True when the step's success criterion was met this attempt.</summary>
    public bool Success { get; }

    /// <summary>Comparison cells for this attempt (may be empty for void/no-throw actions).</summary>
    public CellResult[] Cells { get; }
}

/// <summary>
/// Polls a step's success criterion until it converges or times out. Generated code supplies
/// the attempt as a delegate; this runner owns the retry loop, timing, exception swallowing,
/// and the converged/timed-out annotations that flow into the structured cells + AI JSON.
/// </summary>
public static class WaitForRunner
{
    public static async Task Poll(
        StepResult result,
        int timeoutMs,
        int pollAtMs,
        Func<CancellationToken, Task<WaitAttempt>> attempt,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var attempts = 0;
        WaitAttempt last = default;
        var haveLast = false;
        Exception? lastException = null;

        while (true)
        {
            attempts++;
            try
            {
                last = await attempt(ct);
                haveLast = true;
                lastException = null;

                if (last.Success)
                {
                    var note = $"converged after {sw.ElapsedMilliseconds}ms ({attempts} polls)";
                    MarkCells(result, last.Cells, note, "waitFor", ResultStatus.success);
                    result.MarkSuccess();
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                haveLast = false;
            }

            if (sw.ElapsedMilliseconds >= timeoutMs || ct.IsCancellationRequested)
            {
                var note = $"timed out after {timeoutMs}ms @{pollAtMs}ms ({attempts} attempts)";
                if (lastException != null)
                    note += $"; last error: {lastException.Message}";

                if (haveLast && last.Cells.Length > 0)
                    MarkCells(result, last.Cells, note, "waitFor", ResultStatus.failed);
                else
                    result.MarkCells(new CellResult("waitFor", ResultStatus.failed) { Note = note });

                result.MarkFailed();
                return;
            }

            try
            {
                await Task.Delay(pollAtMs, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    private static void MarkCells(StepResult result, CellResult[] cells, string note,
        string synthName, ResultStatus synthStatus)
    {
        if (cells.Length == 0)
        {
            result.MarkCells(new CellResult(synthName, synthStatus) { Note = note });
            return;
        }

        var annotated = new CellResult[cells.Length];
        for (var i = 0; i < cells.Length; i++)
            annotated[i] = cells[i].WithNote(note);

        result.MarkCells(annotated);
    }
}
