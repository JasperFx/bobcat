using System.Text.Json.Serialization;

namespace Bobcat.Monitoring;

/// <summary>
/// Client-side mirrors of the Bobcat.Monitor ingestion contracts
/// (src/Bobcat.Monitor/Contracts/MonitorEvents.cs). The wire shape — snake_case type
/// discriminator plus camelCase fields — is the contract, deliberately not a shared assembly:
/// Bobcat must stay free of the monitor's Wolverine stack, and the monitor must never be a
/// dependency of the thing it watches. A round-trip test in Bobcat.Monitor.Tests keeps the two
/// sides honest.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStarted), "run_started")]
[JsonDerivedType(typeof(RunHeartbeat), "run_heartbeat")]
[JsonDerivedType(typeof(RunFinished), "run_finished")]
[JsonDerivedType(typeof(ScenarioStarted), "scenario_started")]
[JsonDerivedType(typeof(ScenarioFinished), "scenario_finished")]
[JsonDerivedType(typeof(RetryScheduled), "retry_scheduled")]
[JsonDerivedType(typeof(StepStarted), "step_started")]
[JsonDerivedType(typeof(StepFinished), "step_finished")]
[JsonDerivedType(typeof(LaneStarted), "lane_started")]
[JsonDerivedType(typeof(LaneFinished), "lane_finished")]
[JsonDerivedType(typeof(ResourceRecycled), "resource_recycled")]
[JsonDerivedType(typeof(WorkerFaulted), "worker_faulted")]
public abstract record MonitorEvent(Guid RunId);

public record RunStarted(
    Guid RunId,
    string Suite,
    string Repository,
    string? Branch,
    string Mode,
    DateTimeOffset StartedAt,
    int? TotalScenarios,
    // An opaque correlation tag from BOBCAT_RUN_TAG, passed through verbatim and never
    // interpreted here — an external tool (a ticket id, a coordination plan node) uses it to
    // find this run among the rest. Optional and additive: old publishers simply omit it.
    string? Tag = null) : MonitorEvent(RunId);

public record RunHeartbeat(Guid RunId, DateTimeOffset At) : MonitorEvent(RunId);

public record RunFinished(
    Guid RunId,
    int ExitCode,
    int Passed,
    int Failed,
    int PassedOnRetry,
    int Indeterminate,
    DateTimeOffset FinishedAt) : MonitorEvent(RunId);

public record ScenarioStarted(
    Guid RunId,
    string Uid,
    string Feature,
    string Scenario,
    int Attempt,
    DateTimeOffset At) : MonitorEvent(RunId);

public record ScenarioFinished(
    Guid RunId,
    string Uid,
    string Outcome,
    int Attempts,
    long DurationMs,
    string? ErrorMessage) : MonitorEvent(RunId);

public record RetryScheduled(
    Guid RunId,
    string Uid,
    int NextAttempt,
    string Disposition,
    string Reason) : MonitorEvent(RunId);

public record StepStarted(
    Guid RunId,
    string Uid,
    string StepId,
    string Kind,
    string Text) : MonitorEvent(RunId);

public record StepFinished(
    Guid RunId,
    string Uid,
    string StepId,
    string Status,
    long DurationMs,
    string? ErrorMessage) : MonitorEvent(RunId);

// The supervisor's lane topology, recycles and worker faults (issue #84) — posted by
// SupervisorRunPublisher, which is the only publisher that knows them.

public record LaneStarted(
    Guid RunId,
    int Lane,
    IReadOnlyList<string> Uids,
    DateTimeOffset At) : MonitorEvent(RunId);

public record LaneFinished(
    Guid RunId,
    int Lane,
    int Outcomes,
    bool Crashed,
    DateTimeOffset At) : MonitorEvent(RunId);

public record ResourceRecycled(
    Guid RunId,
    string Resource,
    DateTimeOffset At) : MonitorEvent(RunId);

public record WorkerFaulted(
    Guid RunId,
    int? Lane,
    string Fault,
    int? ExitCode,
    string? StandardError,
    DateTimeOffset At) : MonitorEvent(RunId);
