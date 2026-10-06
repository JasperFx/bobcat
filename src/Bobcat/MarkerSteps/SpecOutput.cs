using Bobcat.Engine;
using Bobcat.Rendering;

namespace Bobcat;

/// <summary>
/// The test runner's own per-test output, as a sink a specification can write to (issue #409).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the runner's output and not a console of our own.</b> A projected suite's author runs
/// under <c>dotnet test</c> or in an IDE, and that is exactly where
/// <see cref="ProjectedSpecConsole"/> deliberately stays silent — a captured stream belongs to
/// whoever captured it. So the one place a developer actually looks when a test goes red is the
/// platform's output block for that test, and nothing Bobcat knew was reaching it.
/// </para>
/// <para>
/// <b>Core names neither runner's types.</b> The sink is an <see cref="Action{T}"/> of lines,
/// supplied by the adapter: <c>Bobcat.Xunit</c> hands over
/// <c>TestContext.Current.TestOutputHelper</c> and <c>Bobcat.TUnit</c> its own writer — there is no
/// <c>ITestOutputHelper</c> in TUnit, which is the reason the seam is a delegate rather than an
/// interface borrowed from one of them. It is the third thing on the marker-step adapter seam,
/// after the two verdict callbacks.
/// </para>
/// <para>
/// <b><see cref="AsyncLocal{T}"/>, because tests run in parallel.</b> A static field would send one
/// test's lines to another test's output under any parallel runner, which is worse than sending
/// them nowhere.
/// </para>
/// <para>
/// <b>Logs go through as they happen; reports go at the end.</b> A log line is written immediately,
/// so a test that also writes to its own output reads as one stream in source order rather than as
/// two blocks. Reports are an account of the whole scenario and cannot exist until it closes, so
/// they are written from <see cref="ScenarioRecorder.ScenarioCompleted"/> — after the verdict is
/// known, which is also what <see cref="ScenarioReportVisibility"/> needs in order to decide.
/// </para>
/// <para>
/// <b>Writing can never fail a test.</b> Every call is guarded: a runner whose output helper has
/// already been torn down throws, and a specification that reported something must not become a
/// failure because the reporting channel closed first. Same invariant as the monitor publisher.
/// </para>
/// </remarks>
public static class SpecOutput
{
    private static readonly AsyncLocal<Action<string>?> _sink = new();
    private static readonly object _gate = new();
    private static bool _subscribed;

    /// <summary>Whether a per-test sink is open on this async context.</summary>
    public static bool IsOpen => _sink.Value is not null;

    /// <summary>
    /// Send this test's specification output to <paramref name="sink"/> until the returned scope is
    /// disposed. Called by a runner adapter around one test.
    /// </summary>
    public static IDisposable Open(Action<string> sink)
    {
        ensureSubscribed();

        var previous = _sink.Value;
        _sink.Value = sink;
        return new Scope(previous);
    }

    /// <summary>Write one line. A no-op when no adapter supplied a sink.</summary>
    public static void Write(string line)
    {
        var sink = _sink.Value;
        if (sink is null) return;

        try
        {
            sink(line);
        }
        catch
        {
            // Never the cause of a red test. See the invariant above.
        }
    }

    /// <summary>
    /// Write a scenario's reports. Takes the render model rather than the reports themselves,
    /// deliberately: <see cref="SpecRender.Reports"/> has already applied
    /// <see cref="ScenarioReportVisibility"/> and already knows whether the scenario failed, so
    /// this cannot reach a different answer than the console and the JSON report did.
    /// </summary>
    public static void WriteReports(SpecRender spec)
    {
        if (_sink.Value is null) return;

        foreach (var report in spec.Reports)
        {
            Write("");
            foreach (var line in TextGrid.Render(report)) Write(line);
        }
    }

    /// <summary>
    /// Subscribe once per process, lazily — a suite with no adapter sink never pays for the
    /// subscription, and a suite with one gets it before its first test closes.
    /// </summary>
    private static void ensureSubscribed()
    {
        lock (_gate)
        {
            if (_subscribed) return;
            _subscribed = true;

            ScenarioRecorder.ScenarioCompleted += recording =>
            {
                // Guarded before building the render model: a suite whose adapter opened a sink for
                // one test must not pay for the fold on every other one.
                if (_sink.Value is null || recording.Reports.Count == 0) return;

                WriteReports(SpecRender.FromRecording(recording));
            };
        }
    }

    private sealed class Scope(Action<string>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sink.Value = previous;
        }
    }
}
