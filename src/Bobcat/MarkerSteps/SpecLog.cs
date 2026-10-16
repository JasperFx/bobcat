using Bobcat.Engine;

namespace Bobcat;

/// <summary>
/// Write a log line onto the step in progress — or onto the scenario, when no step is open — so
/// that a red specification can answer "what did the system do?" (bobcat#547, Storyteller's
/// <c>Reporting.Log</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Static and ambient for the reason <see cref="SpecAssert"/> and <see cref="SpecReport"/>
/// are:</b> a step helper, or a vocabulary in another package, has no context object to be handed,
/// and must compile once against one surface. The scenario is found through
/// <see cref="ScenarioRecorder.Current"/>, which is <c>AsyncLocal</c>, so parallel tests never log
/// into each other.
/// </para>
/// <para>
/// <b>Latched like reports.</b> The lines are recorded always and written out only for a scenario
/// that failed, unless the run is verbose (<c>BOBCAT_VERBOSE_REPORTS</c>) — one
/// rule, <see cref="ScenarioReportVisibility.ShouldWriteLogs"/>, which the console, the JSON report,
/// the runner's test output and the monitor wire all ask.
/// </para>
/// <para>
/// <b>Capped</b> at <see cref="StepLog.MaxLines"/> per step, with "…and N more" — never a silent
/// truncation.
/// </para>
/// <para>
/// <b>Writing can never fail a test.</b> With no scenario open it does nothing, and nothing it does
/// throws.
/// </para>
/// </remarks>
public static class SpecLog
{
    /// <summary>
    /// Record <paramref name="message"/> on the step in progress, or on the scenario between steps.
    /// A no-op outside a scenario.
    /// </summary>
    public static void Write(string? message)
    {
        try
        {
            ScenarioRecorder.Current?.WriteLog(message ?? "");
        }
        catch
        {
            // Never the cause of a red test.
        }
    }

    /// <summary>Whether a scenario is listening — for a caller whose message is expensive to build.</summary>
    public static bool IsRecording => ScenarioRecorder.Current is not null;
}
