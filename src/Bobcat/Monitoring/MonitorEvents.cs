using System.Text.Json.Serialization;

namespace Bobcat.Monitoring;

/// <summary>
/// Client-side mirrors of the Bobcat.Console ingestion contracts
/// (src/Bobcat.Console/Contracts/MonitorEvents.cs). The wire shape — snake_case type
/// discriminator plus camelCase fields — is the contract, deliberately not a shared assembly:
/// Bobcat must stay free of the monitor's Wolverine stack, and the monitor must never be a
/// dependency of the thing it watches. A round-trip test in Bobcat.Console.Tests keeps the two
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
[JsonDerivedType(typeof(StepProgress), "step_progress")]
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
    DateTimeOffset At,
    // How many steps this attempt will run — known up front because the plan is built before
    // the scenario is announced. Null from a publisher that predates it. Optional and additive.
    int? TotalSteps = null) : MonitorEvent(RunId);

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
    string Text,
    // 1-based position of this step within the attempt, and the attempt's step count, so a
    // watcher renders "step 3 of 9" without counting events it may have missed. Null from an
    // older publisher. Optional and additive.
    int? StepNumber = null,
    int? TotalSteps = null,
    // Milliseconds into the scenario's wall clock when this step started.
    long? ScenarioElapsedMs = null) : MonitorEvent(RunId);

public record StepFinished(
    Guid RunId,
    string Uid,
    string StepId,
    string Status,
    long DurationMs,
    string? ErrorMessage,
    // Milliseconds into the scenario's wall clock when this step finished. Optional and additive.
    long? ScenarioElapsedMs = null) : MonitorEvent(RunId);

/// <summary>
/// Interim progress from a step still running — the wire form of
/// <c>IExecutionObserver.StepProgress</c>. Two shapes share it: a <c>[TableGrammar]</c> ticking
/// through its rows (<see cref="Row"/>/<see cref="TotalRows"/>, no message), and a
/// <c>[WaitFor]</c> poll loop reporting what it last saw (<see cref="Message"/>, no rows).
/// <see cref="ElapsedMs"/> is time since the step started. Coalesced by the publisher, so a
/// 200-row grammar does not cost 200 HTTP payloads; the last row always posts.
/// </summary>
public record StepProgress(
    Guid RunId,
    string Uid,
    string StepId,
    string? Message,
    int? Row,
    int? TotalRows,
    long ElapsedMs) : MonitorEvent(RunId);

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
