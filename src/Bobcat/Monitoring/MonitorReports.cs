using Bobcat.Engine;

namespace Bobcat.Monitoring;

/// <summary>
/// The one place an <see cref="IScenarioReport"/> becomes a <see cref="ScenarioReportInfo"/>
/// (issue #408) — the sibling of <see cref="StepCells"/>, and single for the same reason.
/// </summary>
/// <remarks>
/// Both lanes publish reports: the Gherkin lane from <c>MonitorPublishingObserver</c> and the
/// projected lane from <c>ScenarioRecorder</c>. Two copies of this projection is exactly what
/// issue #396 found had quietly dropped a cell's plain text in both lanes at once, so there is
/// one, and it applies <see cref="ScenarioReportVisibility"/> itself rather than trusting each
/// caller to remember.
/// </remarks>
public static class MonitorReports
{
    /// <summary>
    /// The reports worth publishing, or <b>null</b> when there are none. Null rather than an empty
    /// list, so a scenario that reported nothing and a publisher too old to know about reports look
    /// the same to a consumer — which they should, because they mean the same thing.
    /// </summary>
    public static IReadOnlyList<ScenarioReportInfo>? From(
        IReadOnlyList<IScenarioReport> reports, bool scenarioFailed)
    {
        if (reports.Count == 0) return null;

        var visible = ScenarioReportVisibility.Filter(reports, scenarioFailed);
        if (visible.Count == 0) return null;

        return visible
            .Select(r => new ScenarioReportInfo(
                r.Title,
                r.Columns.ToList(),
                r.Cells.Select(StepCells.From).ToList(),
                r.ShortTitle,
                r.SuppressedRows))
            .ToList();
    }
}
