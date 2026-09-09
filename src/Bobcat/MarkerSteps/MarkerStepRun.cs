using System.Reflection;
using Bobcat.Monitoring;
using Bobcat.Resilience;

namespace Bobcat;

/// <summary>
/// The runner-neutral half of a marker-step adapter (issue #110): the process-wide run bracket a
/// scenario is opened inside, and the verdict handling that closes one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is in core rather than in each adapter.</b> Everything here is knowledge that only
/// lives in Bobcat — where a run's identity comes from, who owns the run bracket, and what a
/// verdict has to say to be worth publishing. The first hand-rolled adapter (Marten's, which the
/// docs invited people to paste) got all three wrong in ways a green suite could never reveal:
/// it minted its own <c>RunId</c> and dropped <c>BOBCAT_RUN_TAG</c>, so its evidence could not be
/// attributed to the plan node that asked for it; it published a <c>RunStarted</c> it might not
/// own; and it never set <see cref="ScenarioRecorder.Recording.Failure"/>, so every scenario it
/// ever reported was a <c>CleanPass</c>.
/// </para>
/// <para>
/// An adapter package is therefore only the two callbacks its runner happens to spell differently.
/// </para>
/// <para>
/// <b>Inert when nothing is listening.</b> With no console on the wire the publisher is null and
/// the whole path costs a few strings per test.
/// </para>
/// </remarks>
public static class MarkerStepRun
{
    private static readonly object _gate = new();
    private static IMonitorEventSink? _sink;
    private static MonitorRunInfo? _info;
    private static Timer? _heartbeat;
    private static bool _started;
    private static int _passed;
    private static int _failed;

    /// <summary>How often the run posts a heartbeat while it owns the bracket.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Open a scenario for <paramref name="method"/>, starting the run bracket on first use.
    /// </summary>
    /// <param name="method">The test method. Its name and declaring type supply the identity.</param>
    /// <param name="mode">
    /// How the run was executed — "xunit", "tunit". Recorded on <c>RunStarted</c>; the first call
    /// in a process fixes it, because a run has one mode.
    /// </param>
    public static ScenarioRecorder.Recording BeginScenario(MethodInfo method, string mode)
        => BeginScenario(method.DeclaringType, method.Name, mode);

    /// <summary>
    /// Open a scenario for a test the runner identifies by type and method name rather than by
    /// reflection.
    /// </summary>
    /// <remarks>
    /// Not a convenience overload — TUnit is why it exists. Its <c>TestDetails</c> carries
    /// <c>ClassType</c> and <c>MethodName</c> and hands out no <see cref="MethodInfo"/> at all,
    /// which is the price of being AOT-friendly. Any adapter can reach this shape; not every
    /// adapter can reach the other one.
    /// </remarks>
    public static ScenarioRecorder.Recording BeginScenario(Type? declaringType, string methodName, string mode)
    {
        var info = ensureStarted(mode);

        return ScenarioRecorder.Begin(
            FeatureNameFor(declaringType), Prettify(methodName), _sink, info.RunId);
    }

    /// <summary>
    /// Close the scenario in progress with the verdict its runner reported.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="ScenarioRecorder.Current"/> rather than taking a handle: the recording is
    /// already ambient (it has to be, for a generated interceptor to find it), so an adapter that
    /// held its own reference would be keeping per-test state on an attribute instance the runner
    /// is free to share.
    /// </remarks>
    public static void EndScenario(ScenarioVerdict verdict)
    {
        var recording = ScenarioRecorder.Current;
        if (recording is null) return;

        switch (verdict.Kind)
        {
            case ScenarioVerdictKind.NotClaimed:
                // A skipped test asserted nothing. Publishing CleanPass for it would be the same
                // lie as publishing CleanPass for a failure, so the scenario is withdrawn instead.
                recording.Cancel();
                return;

            case ScenarioVerdictKind.Failed:
                recording.FailureDescription = verdict.Describe();
                Interlocked.Increment(ref _failed);
                break;

            default:
                Interlocked.Increment(ref _passed);
                break;
        }

        recording.Dispose();
    }

    /// <summary>The feature title: <c>[BobcatFeature]</c>'s title, else the class name prettified.</summary>
    public static string FeatureNameFor(MethodInfo method) => FeatureNameFor(method.DeclaringType);

    /// <summary>The feature title for a test class.</summary>
    public static string FeatureNameFor(Type? declaringType)
        => declaringType?.GetCustomAttribute<BobcatFeatureAttribute>()?.Title
           ?? Prettify(declaringType?.Name ?? "Specifications");

    /// <summary>The scenario title: the method name prettified.</summary>
    public static string ScenarioNameFor(MethodInfo method) => Prettify(method.Name);

    /// <summary>Underscores are how a test method spells a sentence.</summary>
    public static string Prettify(string name) => name.Replace('_', ' ');

    private static MonitorRunInfo ensureStarted(string mode)
    {
        lock (_gate)
        {
            if (_info is not null) return _info;

            var info = MonitorRunInfo.Discover(mode);
            _info = info;

            // Never let a missing or slow console matter to a test run: TryConnect probes once
            // with a tight timeout and hands back null when nothing answers.
            _sink = MonitorPublisher.TryConnect().GetAwaiter().GetResult();

            if (_sink is not null && !info.HasExternalOwner)
            {
                // TotalScenarios is null on purpose. The runner owns discovery here and has not
                // told us how many tests it intends to run — and a wrong total is worse than none.
                _sink.Post(new RunStarted(
                    info.RunId, info.Suite, info.Repository, info.Branch, info.Mode,
                    DateTimeOffset.UtcNow, TotalScenarios: null, Tag: info.Tag));

                _heartbeat = new Timer(
                    _ => _sink?.Post(new RunHeartbeat(info.RunId, DateTimeOffset.UtcNow)),
                    null, HeartbeatInterval, HeartbeatInterval);

                _started = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => finish();
            }

            return info;
        }
    }

    /// <summary>
    /// Close the run bracket. Runs at process exit rather than after the last scenario, because
    /// nothing here knows which scenario is the last one — the runner does, and it does not say.
    /// </summary>
    private static void finish()
    {
        lock (_gate)
        {
            if (!_started || _info is null) return;
            _started = false;

            stopHeartbeat();

            _sink?.Post(new RunFinished(
                _info.RunId,
                ExitCode: _failed > 0 ? 1 : 0,
                Passed: _passed,
                Failed: _failed,
                PassedOnRetry: 0,
                Indeterminate: 0,
                FinishedAt: DateTimeOffset.UtcNow));

            // Drain before the process goes away, or RunFinished dies in the channel with it.
            if (_sink is IAsyncDisposable disposable)
            {
                disposable.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>
    /// Plain <c>Timer.Dispose()</c> does not wait for an in-flight callback, so a heartbeat could
    /// still post after <c>RunFinished</c>. The wait-handle overload is what makes "no heartbeats
    /// after the run closed" true rather than merely likely — the same rule
    /// <see cref="MonitorPublishingObserver"/> follows.
    /// </summary>
    private static void stopHeartbeat()
    {
        if (_heartbeat is null) return;

        using var stopped = new ManualResetEvent(false);
        if (_heartbeat.Dispose(stopped)) stopped.WaitOne(TimeSpan.FromSeconds(1));
        _heartbeat = null;
    }

    /// <summary>Test seam: forget the run so the next scenario starts a fresh bracket.</summary>
    internal static void Reset(IMonitorEventSink? sink = null, MonitorRunInfo? info = null)
    {
        lock (_gate)
        {
            stopHeartbeat();
            _sink = sink;
            _info = info;
            _started = false;
            _passed = 0;
            _failed = 0;
        }
    }

    /// <summary>Test seam: the counts <c>RunFinished</c> would report right now.</summary>
    internal static (int Passed, int Failed) Counts => (_passed, _failed);

    /// <summary>Test seam: publish the run bracket's close without waiting for process exit.</summary>
    internal static void FinishForTesting() => finish();

    /// <summary>Test seam: start the bracket with whatever sink <see cref="Reset"/> installed.</summary>
    internal static void StartForTesting(MonitorRunInfo info)
    {
        lock (_gate)
        {
            _info = info;

            if (_sink is not null && !info.HasExternalOwner)
            {
                _sink.Post(new RunStarted(
                    info.RunId, info.Suite, info.Repository, info.Branch, info.Mode,
                    DateTimeOffset.UtcNow, TotalScenarios: null, Tag: info.Tag));
                _started = true;
            }
        }
    }
}

/// <summary>What a runner said about a test, in the only three shapes Bobcat can act on.</summary>
public enum ScenarioVerdictKind
{
    /// <summary>The test ran and passed.</summary>
    Passed,

    /// <summary>The test ran and failed.</summary>
    Failed,

    /// <summary>
    /// Skipped, or never run. The scenario made no claim, so it is withdrawn rather than reported
    /// — <see cref="RunOutcome"/> has no vocabulary for "did not happen", and the nearest word it
    /// does have (<c>CleanPass</c>) would be false.
    /// </summary>
    NotClaimed
}

/// <summary>
/// A runner's verdict, translated out of that runner's vocabulary and into Bobcat's.
/// </summary>
/// <remarks>
/// The exception details are carried as strings because that is what the runners hand over:
/// xUnit v3 reports <c>ExceptionTypes</c> and <c>ExceptionMessages</c> on
/// <c>TestContext.Current.TestState</c>, not the exception itself.
/// </remarks>
public readonly record struct ScenarioVerdict(
    ScenarioVerdictKind Kind,
    string? FailureType = null,
    string? FailureMessage = null)
{
    public static ScenarioVerdict Passed { get; } = new(ScenarioVerdictKind.Passed);

    public static ScenarioVerdict NotClaimed { get; } = new(ScenarioVerdictKind.NotClaimed);

    public static ScenarioVerdict Failed(string? failureType, string? failureMessage)
        => new(ScenarioVerdictKind.Failed, failureType, failureMessage);

    public static ScenarioVerdict FromException(Exception? exception)
        => exception is null
            ? Passed
            : new ScenarioVerdict(ScenarioVerdictKind.Failed, exception.GetType().FullName, exception.Message);

    /// <summary>The one-line failure a viewer shows. Never null for a failed verdict.</summary>
    public string Describe()
        => (FailureType, FailureMessage) switch
        {
            (null or "", null or "") => "Failed",
            (null or "", var message) => message!,
            (var type, null or "") => type!,
            var (type, message) => $"{type}: {message}"
        };
}
