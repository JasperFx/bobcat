using System.Diagnostics;
using JasperFx.Testing;
using Bobcat.Engine;
using Bobcat.Resilience;
using Bobcat.Runtime;

namespace Bobcat.Monitoring;

/// <summary>
/// Maps <see cref="IExecutionObserver"/> callbacks onto the monitor's wire events. Attached via
/// <see cref="BobcatRunner.AddObserver"/> so it rides alongside whatever observer a front-end
/// already registered (the MTP host's node publisher, say).
/// </summary>
/// <remarks>
/// Not thread-safe by design: the in-process runner executes scenarios sequentially, and the
/// current-scenario tracking here relies on that. Revisit if the runner ever runs scenarios
/// concurrently in one process.
/// </remarks>
public sealed class MonitorPublishingObserver : IExecutionObserver, IAsyncDisposable
{
    private readonly IMonitorEventSink _sink;
    private readonly MonitorRunInfo _info;
    private readonly IAsyncDisposable? _ownedPublisher;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _progressInterval;
    private readonly Dictionary<string, int> _attemptsByUid = new();
    private readonly Stopwatch _scenarioClock = new();
    private readonly Stopwatch _stepClock = new();
    private Timer? _heartbeat;
    private string _currentUid = "";
    private int? _totalSteps;
    private int _stepNumber;
    private long _lastProgressPostedAtMs = long.MinValue;

    public MonitorPublishingObserver(
        IMonitorEventSink sink,
        MonitorRunInfo info,
        IAsyncDisposable? ownedPublisher = null,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? progressInterval = null)
    {
        _sink = sink;
        _info = info;
        _ownedPublisher = ownedPublisher;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(10);
        _progressInterval = progressInterval ?? DefaultProgressInterval;
    }

    /// <summary>
    /// Minimum spacing between two <see cref="StepProgress"/> posts for one step. A 200-row
    /// grammar whose rows take a millisecond each would otherwise post 200 events into a channel
    /// that drops on backpressure — and the events it would crowd out (<see cref="StepFinished"/>,
    /// <see cref="ScenarioFinished"/>) matter more than any single row tick. The first update of
    /// a step and the last row always post, so a watcher never misses the start or the end.
    /// </summary>
    public static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromMilliseconds(100);

    public void RunStarted(int totalScenarios)
    {
        // A participant's bracket belongs to the run's owner (the supervisor): posting our own
        // RunStarted would overwrite the owner's suite name and true scenario total.
        if (_info.HasExternalOwner) return;

        _sink.Post(new RunStarted(
            _info.RunId, _info.Suite, _info.Repository, _info.Branch, _info.Mode,
            DateTimeOffset.UtcNow, totalScenarios, _info.Tag));

        _heartbeat = new Timer(
            _ => _sink.Post(new RunHeartbeat(_info.RunId, DateTimeOffset.UtcNow)),
            null, _heartbeatInterval, _heartbeatInterval);
    }

    public void RunFinished(SuiteResults results)
    {
        // The first worker finishing must not mark the shared run finished with its own
        // partial counts — the owner posts the terminal event when the whole run settles.
        if (_info.HasExternalOwner) return;

        stopHeartbeat();

        var scenarios = results.AllScenarios.ToArray();
        _sink.Post(new RunFinished(
            _info.RunId,
            results.ExitCode,
            Passed: scenarios.Count(s => s.Outcome == RunOutcome.CleanPass),
            Failed: scenarios.Count(s => s.Outcome is RunOutcome.Failed or RunOutcome.Aborted),
            PassedOnRetry: scenarios.Count(s => s.Outcome == RunOutcome.PassOnRetry),
            // The in-process runner always knows what happened; Indeterminate is a
            // supervisor-only concept (a crashed worker).
            Indeterminate: 0,
            DateTimeOffset.UtcNow));
    }

    // An observer upstream that only knows the two-argument form (a hand-rolled harness calling
    // this directly) still gets a ScenarioStarted on the wire — just without the step count.
    public void ScenarioStarted(string featureTitle, string scenarioTitle)
        => scenarioStarted(featureTitle, scenarioTitle, totalSteps: null);

    public void ScenarioStarted(string featureTitle, string scenarioTitle, int totalSteps)
        => scenarioStarted(featureTitle, scenarioTitle, totalSteps);

    private void scenarioStarted(string featureTitle, string scenarioTitle, int? totalSteps)
    {
        // Same identity formula as SpecNodeMapping.Uid and the retry budget's test id.
        _currentUid = $"{featureTitle}/{scenarioTitle}";

        var attempt = _attemptsByUid.TryGetValue(_currentUid, out var previous) ? previous + 1 : 1;
        _attemptsByUid[_currentUid] = attempt;

        _totalSteps = totalSteps;
        _stepNumber = 0;
        _scenarioClock.Restart();

        _sink.Post(new ScenarioStarted(
            _info.RunId, _currentUid, featureTitle, scenarioTitle, attempt, DateTimeOffset.UtcNow, totalSteps));
    }

    public void StepStarted(string stepId, StepKind kind, string stepText)
    {
        _stepNumber++;
        _stepClock.Restart();
        _lastProgressPostedAtMs = long.MinValue;

        _sink.Post(new StepStarted(
            _info.RunId, _currentUid, stepId, kind.ToString(), stepText,
            StepNumber: _stepNumber,
            TotalSteps: _totalSteps,
            ScenarioElapsedMs: _scenarioClock.ElapsedMilliseconds));
    }

    /// <summary>
    /// Interim progress onto the wire — a <c>[TableGrammar]</c> row tick or a <c>[WaitFor]</c>
    /// poll message — coalesced per <see cref="DefaultProgressInterval"/> so a fast grammar
    /// cannot flood the channel. The first update of a step and the final row always post.
    /// </summary>
    public void StepProgress(string stepId, StepUpdate update)
    {
        var elapsed = _stepClock.ElapsedMilliseconds;
        var isLastRow = update.Row.HasValue && update.Row == update.TotalRows;
        var dueAtMs = _lastProgressPostedAtMs == long.MinValue
            ? long.MinValue
            : _lastProgressPostedAtMs + (long)_progressInterval.TotalMilliseconds;

        if (!isLastRow && elapsed < dueAtMs) return;

        _lastProgressPostedAtMs = elapsed;
        _sink.Post(new Monitoring.StepProgress(
            _info.RunId, _currentUid, stepId, update.Message, update.Row, update.TotalRows, elapsed));
    }

    public void StepFinished(StepResult result)
        => _sink.Post(new StepFinished(
            _info.RunId, _currentUid, result.StepId,
            result.StepStatus.ToString(),
            Math.Max(0, result.End - result.Start),
            result.Exception?.Message,
            ScenarioElapsedMs: _scenarioClock.ElapsedMilliseconds));

    public void ScenarioRetrying(string scenarioTitle, int nextAttempt, string reason)
        // The in-process runner only ever performs in-process retries — fresh-process and
        // recycle dispositions are recorded as unsupported, not retried.
        => _sink.Post(new RetryScheduled(
            _info.RunId, _currentUid, nextAttempt, nameof(DispositionKind.RetryInProcess), reason));

    public void ScenarioCompleted(string featureTitle, ScenarioResult result)
    {
        var uid = $"{featureTitle}/{result.Title}";
        _sink.Post(new ScenarioFinished(
            _info.RunId,
            uid,
            result.Outcome.ToString(),
            result.AttemptCount,
            (long)(result.Results.EndTime - result.Results.StartTime).TotalMilliseconds,
            result.Results.AllExceptions().FirstOrDefault()?.Message));
    }

    public void FeatureStarted(string featureTitle) { }
    public void FeatureFinished(string featureTitle) { }
    public void ScenarioFinished(Engine.ExecutionResults results) { }

    /// <summary>
    /// Plain Timer.Dispose() does NOT wait for an in-flight callback, so a heartbeat could
    /// still post after RunFinished on a busy box — the wait-handle overload makes "no
    /// heartbeats after the run closed" actually true rather than merely likely.
    /// </summary>
    private void stopHeartbeat()
    {
        var timer = _heartbeat;
        _heartbeat = null;
        if (timer == null) return;

        using var drained = new ManualResetEvent(false);
        if (timer.Dispose(drained))
        {
            drained.WaitOne(TimeSpan.FromSeconds(1));
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopHeartbeat();

        if (_ownedPublisher != null) await _ownedPublisher.DisposeAsync();
    }
}
