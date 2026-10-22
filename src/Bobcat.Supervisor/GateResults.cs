using System.Text;
using Bobcat.Runtime;

namespace Bobcat.Supervisor;

/// <summary>
/// What one gate run produced: every unit's result, in the order they were started, the track
/// preflights, and one exit code — the worst of the units (bobcat#729).
/// </summary>
public sealed class GateResults
{
    /// <summary>The units in the order the scheduler started them; every declared unit is here, run or not.</summary>
    public required IReadOnlyList<GateUnitResult> Units { get; init; }

    /// <summary>Each track's preflight, in declaration order. Only tracks a unit names are checked.</summary>
    public required IReadOnlyList<TrackPreflight> Preflight { get; init; }

    /// <summary>The plan as it was printed: the order units were offered to the budget.</summary>
    public required IReadOnlyList<GateUnit> Plan { get; init; }

    /// <summary>Why nothing ran, when nothing did — a failed preflight, a failed build, a missing host.</summary>
    public string? AbortReason { get; init; }

    public int WorkerBudget { get; init; }

    /// <summary>Wall clock for the whole gate: preflight, build and the units.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>What the units would have taken one after the other — the figure the makespan is set against.</summary>
    public TimeSpan SequentialDuration => TimeSpan.FromTicks(Units.Sum(u => u.Duration.Ticks));

    /// <summary>
    /// The worst unit's exit code, in the supervisor's own convention: 2 (indeterminate, aborted,
    /// or a unit that never ran) over 1 (failed) over 0. A gate that aborted before any unit ran
    /// is 2 — nothing is known about the tests, which is not an ordinary red build.
    /// </summary>
    public int ExitCode
    {
        get
        {
            if (AbortReason is not null) return 2;
            if (Units.Count == 0) return 0;
            return Units.Max(u => u.ExitCode);
        }
    }

    public IReadOnlyList<GateUnitResult> Clean => Units.Where(u => u.Verdict == GateVerdict.Passed).ToList();
    public IReadOnlyList<GateUnitResult> Flaky => Units.Where(u => u.Verdict == GateVerdict.PassedOnRetry).ToList();
    public IReadOnlyList<GateUnitResult> Failed => Units.Where(u => u.Verdict == GateVerdict.Failed).ToList();
    public IReadOnlyList<GateUnitResult> Indeterminate => Units.Where(u => u.Verdict == GateVerdict.Indeterminate).ToList();
    public IReadOnlyList<GateUnitResult> NotRun => Units.Where(u => u.Verdict == GateVerdict.NotRun).ToList();

    /// <summary>One line per unit, predicted against actual, then each unit's full report for anything not clean.</summary>
    public string ToText()
    {
        var text = new StringBuilder();

        text.AppendLine($"Gate: {Units.Count} unit(s), {WorkerBudget} worker(s) budgeted, {Humanize(Duration)} wall clock" +
                        (Units.Count > 1 ? $" ({Humanize(SequentialDuration)} one after the other)" : ""));

        foreach (var preflight in Preflight.Where(p => !p.Results.Succeeded()))
        {
            text.AppendLine($"  ✗ track {preflight.Track.Name}:");
            text.Append(Bobcat.Runtime.Preflight.Describe(preflight.Results));
        }

        if (AbortReason is not null) text.AppendLine($"  ABORTED: {AbortReason}");

        var width = Units.Count == 0 ? 0 : Units.Max(u => u.Unit.Name.Length);
        foreach (var unit in Units)
        {
            text.AppendLine($"  {unit.Mark} {unit.Unit.Name.PadRight(width)}  {Humanize(unit.Duration),8}" +
                            (unit.Predicted is { } predicted ? $" (predicted {Humanize(predicted)})" : " (first run)") +
                            $"  {unit.Summary}");
        }

        foreach (var unit in Units.Where(u => u.Verdict is not GateVerdict.Passed and not GateVerdict.NotRun))
        {
            text.AppendLine();
            text.AppendLine($"=== {unit.Unit.Name} ===");
            if (unit.Error is not null) text.AppendLine(unit.Error.ToString());
            if (unit.Results is not null) text.Append(RunReport.ToText(unit.Results));
        }

        text.AppendLine();
        text.AppendLine($"Exit code {ExitCode}: {Clean.Count} clean, {Flaky.Count} passed on retry, {Failed.Count} failed, " +
                        $"{Indeterminate.Count} indeterminate, {NotRun.Count} not run.");

        return text.ToString();
    }

    internal static string Humanize(TimeSpan duration)
        => duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h{duration.Minutes:00}m"
            : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s"
            : duration.TotalSeconds >= 10 ? $"{duration.TotalSeconds:0}s"
            : $"{duration.TotalSeconds:0.0}s";
}

/// <summary>One track's preflight outcome.</summary>
public sealed record TrackPreflight(Track Track, PreflightResults Results);

public enum GateVerdict
{
    /// <summary>Every test passed first time (or no test ran and the project allows that).</summary>
    Passed,

    /// <summary>Exit 0, but at least one test needed a retry — reported, never folded into clean.</summary>
    PassedOnRetry,

    Failed,

    /// <summary>Exit 2: aborted, a worker died, or the supervisor itself threw.</summary>
    Indeterminate,

    /// <summary>The gate stopped before this unit started.</summary>
    NotRun
}

/// <summary>One unit's result.</summary>
public sealed record GateUnitResult(
    GateUnit Unit,
    SupervisorResults? Results,
    TimeSpan Duration,
    TimeSpan? Predicted,
    int Workers,
    string? RunTag,
    Exception? Error = null)
{
    /// <summary>The unit's exit code: the supervisor's, 2 when it threw, and 2 when it never ran.</summary>
    public int ExitCode => Results?.ExitCode ?? 2;

    public GateVerdict Verdict
    {
        get
        {
            if (Results is null) return Error is null && Duration == TimeSpan.Zero ? GateVerdict.NotRun : GateVerdict.Indeterminate;
            if (Results.ExitCode == 2) return GateVerdict.Indeterminate;
            if (Results.ExitCode == 1) return GateVerdict.Failed;
            return Results.PassedOnRetry.Count > 0 ? GateVerdict.PassedOnRetry : GateVerdict.Passed;
        }
    }

    internal string Mark => Verdict switch
    {
        GateVerdict.Passed => "✓",
        GateVerdict.PassedOnRetry => "~",
        GateVerdict.Failed => "✗",
        GateVerdict.Indeterminate => "?",
        _ => "-"
    };

    /// <summary>A short account for the one-line report.</summary>
    public string Summary
    {
        get
        {
            if (Error is not null) return $"ERROR {Error.GetType().Name}: {Error.Message}";
            if (Results is null) return "not run";
            if (Results.AbortReason is not null) return $"ABORTED {Results.AbortReason}";

            var parts = new List<string> { $"{Results.CleanPasses.Count} passed" };
            if (Results.PassedOnRetry.Count > 0) parts.Add($"{Results.PassedOnRetry.Count} passed on retry");
            if (Results.Failed.Count(t => !t.IsIndeterminate) > 0) parts.Add($"{Results.Failed.Count(t => !t.IsIndeterminate)} failed");
            if (Results.Indeterminate.Count > 0) parts.Add($"{Results.Indeterminate.Count} indeterminate");
            if (Results.Skipped.Count > 0) parts.Add($"{Results.Skipped.Count} skipped");
            parts.Add($"{Workers} worker(s)");
            return string.Join(", ", parts);
        }
    }
}
