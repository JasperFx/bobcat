using Bobcat.Engine;

namespace Bobcat.Monitoring;

/// <summary>
/// The one place a scenario's <see cref="SpecLog"/> lines become <see cref="StepLogInfo"/>s on
/// <see cref="ScenarioFinished"/> (bobcat#547) — the sibling of <see cref="MonitorReports"/>, and it
/// applies <see cref="ScenarioReportVisibility.ShouldWriteLogs"/> itself for the same reason.
/// </summary>
public static class MonitorLogs
{
    /// <summary>
    /// The visible logs — every step that logged, in step order, then the scenario's own — or
    /// <b>null</b> when there are none, so "logged nothing" and "a publisher too old to log" look the
    /// same.
    /// </summary>
    public static IReadOnlyList<StepLogInfo>? From(ScenarioRecorder.Recording recording, bool scenarioFailed)
    {
        if (!ScenarioReportVisibility.ShouldWriteLogs(scenarioFailed)) return null;

        var logs = new List<StepLogInfo>();

        foreach (var step in recording.Steps)
        {
            if (!step.Log.IsEmpty) logs.Add(new StepLogInfo(step.StepId, step.Log.Lines, step.Log.Suppressed));
        }

        if (!recording.Log.IsEmpty) logs.Add(new StepLogInfo(null, recording.Log.Lines, recording.Log.Suppressed));

        return logs.Count == 0 ? null : logs;
    }
}
