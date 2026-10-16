namespace Bobcat.Ui.Runs;

/// <summary>
/// The one place a step's wire status is read as a verdict. Two publisher vocabularies reach
/// <c>step_finished.status</c> and both are live in archives:
/// </summary>
/// <remarks>
/// <para>
/// <strong>The Gherkin lane</strong> sent the step's <c>ResultStatus</c> verbatim — <c>success</c>
/// or <c>ok</c> for a pass, anything else for a failure. <strong>The projected lane</strong>
/// (<c>ScenarioRecorder</c>, every Bobcat since 0.41.0 and the only lane since 0.50.0) sends
/// <c>Passed</c> or <c>Failed</c>. Reading only the first vocabulary drew every step of every
/// passing projected run as a failure — a red ✗ on each line of a green spec (the 2026-10-12
/// CritterCrush rehearsal, Bobcat 0.60.1). The status string itself stays verbatim on the
/// projection; only the reading of it is decided here, and in the Pinia store's mirror of this
/// rule (<c>stepStatusOf</c> in <c>runs-store.ts</c>).
/// </para>
/// </remarks>
public static class StepVerdict
{
    /// <summary>True when the step's wire status says it passed, in either lane's vocabulary.</summary>
    public static bool IsPassed(string? status)
        => status is not null
           && (status.Equals("success", StringComparison.OrdinalIgnoreCase)
               || status.Equals("ok", StringComparison.OrdinalIgnoreCase)
               || status.Equals("passed", StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a step that has a verdict and the verdict is not a pass.</summary>
    public static bool IsFailed(string? status)
        => status != StepProjection.Running && !IsPassed(status);
}
