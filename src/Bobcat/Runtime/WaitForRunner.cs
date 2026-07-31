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
        CancellationToken ct,
        IStepContext? progress = null)
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
                    markCells(result, last.Cells, note, "waitFor", ResultStatus.success);
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
                    markCells(result, last.Cells, note, "waitFor", ResultStatus.failed);
                else
                    result.MarkCells(new CellResult("waitFor", ResultStatus.failed) { Note = note });

                result.MarkFailed();
                return;
            }

            // Non-converged, not yet timed out: surface the latest value live so the step's row
            // animates during a long poll instead of freezing between StepStarted and StepFinished.
            reportAttempt(progress, attempts, sw.ElapsedMilliseconds, last, haveLast, lastException);

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

    private static void reportAttempt(IStepContext? progress, int attempts, long elapsedMs,
        WaitAttempt last, bool haveLast, Exception? lastException)
    {
        if (progress == null) return;

        var prefix = $"waiting… (attempt {attempts}, {elapsedMs}ms)";

        if (lastException != null)
        {
            progress.ReportProgress(new StepUpdate($"{prefix}; last error: {lastException.Message}"));
            return;
        }

        var cells = haveLast ? last.Cells : System.Array.Empty<CellResult>();
        var value = describeLast(cells);
        var message = value == null ? prefix : $"{prefix}; last value {value}";
        progress.ReportProgress(new StepUpdate(message) { Cells = cells });
    }

    private static string? describeLast(CellResult[] cells)
    {
        if (cells.Length == 0) return null;
        if (cells.Length == 1) return cells[0].Actual ?? cells[0].DisplayText;

        var parts = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++)
            parts[i] = $"{cells[i].Name}={cells[i].Actual ?? "?"}";

        return string.Join(", ", parts);
    }

    private static void markCells(StepResult result, CellResult[] cells, string note,
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
