namespace Bobcat.Engine;

/// <summary>
/// Which of a scenario's reports a run actually writes out — the one rule every surface asks
/// (issue #408).
/// </summary>
/// <remarks>
/// <para>
/// <b>One function, so the surfaces cannot disagree.</b> The console, the JSON report and the
/// monitor wire all read the same decision, the discipline <see cref="CellResult.DisplayText"/>
/// already follows for what a cell says. A fourth surface that defaulted to showing everything
/// would make "what this run reported" mean two things depending on where you read it.
/// </para>
/// <para>
/// <b>Settled 2026-10-06: the latch is uniform and <c>--verbose</c> is the only thing that lifts
/// it.</b> `run --json --verbose` is what emits non-failure text, so JSON and the archive do not
/// carry a passing scenario's reports by default either. The alternative considered was for JSON
/// always to carry everything on the argument that an agent reads the archive afterwards and
/// volume there is cheap — rejected because it makes JSON an exception to the rule above, and
/// because an agent that wants everything can ask for it and then gets it on every surface at
/// once.
/// </para>
/// <para>
/// <b>Why the verbose setting is process-wide rather than a parameter.</b> It is a property of how
/// the run was invoked, not of any one scenario, and threading a bool through
/// <see cref="Rendering.SpecRender"/>, both renderers and the publisher would put four copies of
/// one fact in four signatures. <c>BOBCAT_SPEC_CONSOLE</c> is the same shape of decision made the
/// same way. <see cref="ShouldWrite(IScenarioReport,bool,bool)"/> takes it explicitly, so the rule
/// stays testable without a process.
/// </para>
/// </remarks>
public static class ScenarioReportVisibility
{
    /// <summary>
    /// Set this to <c>1</c>/<c>true</c> to write every report whatever the verdict — the
    /// environment's spelling of <c>--verbose</c>, for a suite that cannot pass a flag.
    /// </summary>
    public const string EnvironmentVariable = "BOBCAT_VERBOSE_REPORTS";

    private static bool? _verbose;

    /// <summary>
    /// Whether this run writes non-failure text. Set by <c>--verbose</c>; otherwise read from
    /// <see cref="EnvironmentVariable"/> as a tri-state, so "nobody said" stays distinguishable
    /// from "somebody said no".
    /// </summary>
    public static bool Verbose
    {
        // ProjectedSpecConsole.Setting is the codebase's one BOBCAT_* tri-state reader, so this
        // reuses it rather than keeping a second copy of "0/false/False/FALSE means no" that
        // could drift from it. It lives in the projected lane only because that is where the
        // first such switch happened to be needed.
        get => _verbose ?? (Bobcat.ProjectedSpecConsole.Setting(EnvironmentVariable) ?? false);
        set => _verbose = value;
    }

    /// <summary>Forget an explicit setting, so the environment decides again. For tests.</summary>
    public static void Reset() => _verbose = null;

    /// <summary>The rule itself. Pure, so it can be read and tested without a run.</summary>
    public static bool ShouldWrite(IScenarioReport report, bool scenarioFailed, bool verbose)
        => verbose || report.Visibility == ReportVisibility.Always || scenarioFailed;

    /// <summary>
    /// The reports this scenario writes out, in registration order. A report with no cells is
    /// dropped whatever its visibility: an empty grid under a heading says less than no heading,
    /// and a grammar that was never exercised legitimately produces one.
    /// </summary>
    public static IReadOnlyList<IScenarioReport> Filter(
        IEnumerable<IScenarioReport> reports, bool scenarioFailed, bool? verbose = null)
    {
        var isVerbose = verbose ?? Verbose;

        return reports
            .Where(r => r.Cells.Count > 0 && ShouldWrite(r, scenarioFailed, isVerbose))
            .ToList();
    }
}
