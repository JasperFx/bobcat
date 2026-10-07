namespace Bobcat.Tests.Reporting;

/// <summary>
/// The classes that set or reset the process-wide <see cref="Engine.ScenarioReportVisibility"/>.
/// Run in parallel, one class's <c>Reset()</c> lands inside another's verbose window and a report
/// that should be written is not.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ReportVisibilityCollection
{
    public const string Name = "report-visibility";
}
