namespace Bobcat.Engine;

/// <summary>
/// Where a scenario's reports accumulate. Implemented by the scenario's recording,
/// <see cref="ScenarioRecorder.Recording"/>, the thing that owns a scenario
/// (issue #408).
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
/// <b>Ambient because a report producer has no context to be handed.</b> A helper in
/// <c>WolverineFx.Bobcat</c> is called from a projected <c>[BobcatSpec]</c> test, an ordinary xUnit
/// / TUnit / NUnit / MSTest test with no Bobcat DI scope behind it, so there is nothing to pass the
/// helper and nothing it could resolve a context from. A static ambient surface is the shape that
/// reaches it, and it is already this codebase's answer to exactly this problem:
/// <c>ScenarioRecorder.Current</c> and <c>BobcatClock</c> are both <see cref="AsyncLocal{T}"/> for
/// the same reason.
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
/// The static front for <see cref="IReportSink.ReportFor{TReport}"/>, and the reason it is static is
/// in <see cref="ScenarioReports"/>: a producer in another package has no scenario context to be
/// handed. <c>SpecAssert</c> is the same decision for the same reason.
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
