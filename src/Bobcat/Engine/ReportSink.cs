namespace Bobcat.Engine;

/// <summary>
/// Where a scenario's reports accumulate. Implemented by the Gherkin lane's execution results and
/// the projected lane's recording — the two things that own a scenario (issue #408).
/// </summary>
public interface IReportSink
{
    /// <summary>
    /// This scenario's <typeparamref name="TReport"/>, created on first ask. One instance per
    /// report type per scenario, which is what lets several grammars append to one table while
    /// agreeing on nothing but the type — Storyteller's <c>ReporterFor&lt;T&gt;()</c>.
    /// </summary>
    TReport ReportFor<TReport>() where TReport : IScenarioReport, new();

    /// <summary>
    /// Attach a report built elsewhere. Replaces an earlier report of the same runtime type, so
    /// the one-per-type rule holds however a report arrived.
    /// </summary>
    void AttachReport(IScenarioReport report);

    /// <summary>The reports attached so far, in first-registration order.</summary>
    IReadOnlyList<IScenarioReport> Reports { get; }
}

/// <summary>
/// The scenario whose reports a producer is appending to, ambient on the async context
/// (issue #408).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ambient because a report producer must compile once and work in both lanes.</b> A helper in
/// <c>WolverineFx.Bobcat</c> is called from a <c>.feature</c> fixture, which has an
/// <see cref="IStepContext"/>, and from a projected <c>[BobcatSpec]</c> test, which has none and
/// cannot be given one — there is no Bobcat DI scope or <c>TestResources</c> behind an xUnit test,
/// so <c>GetService&lt;T&gt;()</c> would have to throw, and a context whose half throws is worse
/// than no context. A static ambient surface is the only shape that reaches both, and it is
/// already this codebase's answer to exactly this problem: <c>ScenarioRecorder.Current</c> and
/// <c>BobcatClock</c> are both <see cref="AsyncLocal{T}"/> for the same reason.
/// </para>
/// <para>
/// <b>Silent when nothing opened a scenario.</b> A grammar helper gets called from plenty of places
/// that are not specifications. No scenario, no sink, no throw — the rule
/// <c>ScenarioRecorder</c> already follows.
/// </para>
/// </remarks>
public static class ScenarioReports
{
    private static readonly AsyncLocal<IReportSink?> _current = new();

    /// <summary>The scenario accumulating reports on this async context, or null.</summary>
    public static IReportSink? Current => _current.Value;

    /// <summary>
    /// Make <paramref name="sink"/> the ambient scenario until the returned scope is disposed,
    /// restoring whatever was ambient before.
    /// </summary>
    /// <remarks>
    /// Restores rather than clears, so a nested bracket — a feature-level context around a
    /// scenario-level one — cannot leave the outer scenario unable to report.
    /// </remarks>
    public static IDisposable Open(IReportSink sink)
    {
        var previous = _current.Value;
        _current.Value = sink;
        return new Scope(previous);
    }

    private sealed class Scope(IReportSink? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}

/// <summary>
/// The one report surface a grammar in another package writes against (issue #408).
/// </summary>
/// <remarks>
/// <para>
/// The static twin of <see cref="IStepContext.ReportFor{TReport}"/>, and the reason it is static is
/// in <see cref="ScenarioReports"/>: a producer shared between the Gherkin and projected lanes has
/// an <see cref="IStepContext"/> in one of them and nothing in the other. <c>SpecAssert</c> is the
/// same decision for the same reason.
/// </para>
/// </remarks>
public static class SpecReport
{
    /// <summary>
    /// This scenario's <typeparamref name="TReport"/>, created on first ask — or a throwaway
    /// instance when no scenario is recording, so a helper called outside a specification still
    /// works and simply reports to nobody.
    /// </summary>
    public static TReport For<TReport>() where TReport : IScenarioReport, new()
        => ScenarioReports.Current is { } sink ? sink.ReportFor<TReport>() : new TReport();

    /// <summary>Attach a report to the scenario in progress. A no-op when there is none.</summary>
    public static void Attach(IScenarioReport report)
        => ScenarioReports.Current?.AttachReport(report);

    /// <summary>Whether a scenario is listening — for a producer whose rows are expensive to build.</summary>
    public static bool IsRecording => ScenarioReports.Current is not null;
}
