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
    private readonly Dictionary<string, int> _attemptsByUid = new();
    private Timer? _heartbeat;
    private string _currentUid = "";

    public MonitorPublishingObserver(
        IMonitorEventSink sink,
        MonitorRunInfo info,
        IAsyncDisposable? ownedPublisher = null,
        TimeSpan? heartbeatInterval = null)
    {
        _sink = sink;
        _info = info;
        _ownedPublisher = ownedPublisher;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(10);
    }

    public void RunStarted(int totalScenarios)
    {
        _sink.Post(new RunStarted(
            _info.RunId, _info.Suite, _info.Repository, _info.Branch, _info.Mode,
            DateTimeOffset.UtcNow, totalScenarios));

        _heartbeat = new Timer(
            _ => _sink.Post(new RunHeartbeat(_info.RunId, DateTimeOffset.UtcNow)),
            null, _heartbeatInterval, _heartbeatInterval);
    }

    public void RunFinished(SuiteResults results)
    {
        _heartbeat?.Dispose();
        _heartbeat = null;

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

    public void ScenarioStarted(string featureTitle, string scenarioTitle)
    {
        // Same identity formula as SpecNodeMapping.Uid and the retry budget's test id.
        _currentUid = $"{featureTitle}/{scenarioTitle}";

        var attempt = _attemptsByUid.TryGetValue(_currentUid, out var previous) ? previous + 1 : 1;
        _attemptsByUid[_currentUid] = attempt;

        _sink.Post(new ScenarioStarted(
            _info.RunId, _currentUid, featureTitle, scenarioTitle, attempt, DateTimeOffset.UtcNow));
    }

    public void StepStarted(string stepId, StepKind kind, string stepText)
        => _sink.Post(new StepStarted(_info.RunId, _currentUid, stepId, kind.ToString(), stepText));

    public void StepFinished(StepResult result)
        => _sink.Post(new StepFinished(
            _info.RunId, _currentUid, result.StepId,
            result.StepStatus.ToString(),
            Math.Max(0, result.End - result.Start),
            result.Exception?.Message));

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
    public void StepProgress(string stepId, StepUpdate update) { }
    public void ScenarioFinished(Engine.ExecutionResults results) { }

    public async ValueTask DisposeAsync()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;

        if (_ownedPublisher != null) await _ownedPublisher.DisposeAsync();
    }
}
