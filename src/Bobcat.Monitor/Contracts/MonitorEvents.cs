using JasperFx.Testing;
using System.Text.Json.Serialization;
using Wolverine;

namespace Bobcat.Monitor.Contracts;

/// <summary>
/// One event in the monitor's ingestion stream. Publishers (BobcatRunner, the supervisor,
/// worker processes) POST batches of these to /api/ingest; the monitor relays each one to the
/// browser over SignalR unchanged, so the JSON type discriminator here and the Wolverine
/// message type name the frontend switches on are the same snake_case string by construction.
///
/// Wire rules, in order of importance:
///  1. A publisher must never be slowed or failed by the monitor — events are fire-and-forget,
///     dropped on backpressure, and the whole publisher goes no-op when /api/ping doesn't answer.
///  2. Events are facts about a run, keyed by RunId + the scenario Uid ("{Feature}/{Scenario}"
///     — the same identity string BobcatRunner, RetryBudget, SpecNodeMapping, and WorkPlan
///     already share).
///  3. Enum-ish values travel as strings (mirroring RunOutcome, DispositionKind, StepKind,
///     ResultStatus) so this contract never forces a reference to Bobcat itself. When the
///     publisher client is built inside Bobcat, these records move to a tiny shared
///     contracts package; the wire shape is the contract, not the assembly.
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
public abstract record MonitorEvent(Guid RunId) : WebSocketMessage;

/// <summary>
/// A suite run announced itself. Repository is the root repository path — the dashboard's
/// grouping key when several suites run in parallel on one box. Mode distinguishes an
/// in-process BobcatRunner from a supervised multi-worker run.
/// </summary>
public record RunStarted(
    Guid RunId,
    string Suite,
    string Repository,
    string? Branch,
    string Mode,
    DateTimeOffset StartedAt,
    int? TotalScenarios,
    // An opaque correlation tag from BOBCAT_RUN_TAG, stored and echoed back verbatim. The
    // viewer never interprets it — an external tool stamps a ticket id, build number, or its
    // own node id and finds the run by it over /api/runs?tag=. Optional and additive: old
    // publishers simply omit it.
    string? Tag = null) : MonitorEvent(RunId);

/// <summary>
/// Periodic liveness signal. A run that stops heartbeating without a RunFinished is presumed
/// crashed/orphaned and rendered as such rather than "still running" forever.
/// </summary>
public record RunHeartbeat(Guid RunId, DateTimeOffset At) : MonitorEvent(RunId);

public record RunFinished(
    Guid RunId,
    int ExitCode,
    int Passed,
    int Failed,
    int PassedOnRetry,
    int Indeterminate,
    DateTimeOffset FinishedAt) : MonitorEvent(RunId);

/// <summary>Attempt is 1-based; a value above 1 marks a retry attempt.</summary>
public record ScenarioStarted(
    Guid RunId,
    string Uid,
    string Feature,
    string Scenario,
    int Attempt,
    DateTimeOffset At) : MonitorEvent(RunId);

/// <summary>Outcome mirrors RunOutcome: CleanPass / PassOnRetry / Failed / Aborted.</summary>
public record ScenarioFinished(
    Guid RunId,
    string Uid,
    string Outcome,
    int Attempts,
    long DurationMs,
    string? ErrorMessage) : MonitorEvent(RunId);

/// <summary>Disposition mirrors DispositionKind; Reason is the human-readable policy reason.</summary>
public record RetryScheduled(
    Guid RunId,
    string Uid,
    int NextAttempt,
    string Disposition,
    string Reason) : MonitorEvent(RunId);

/// <summary>Kind mirrors StepKind (Given/When/Then/...).</summary>
public record StepStarted(
    Guid RunId,
    string Uid,
    string StepId,
    string Kind,
    string Text) : MonitorEvent(RunId);

/// <summary>Status mirrors ResultStatus (ok/success/failed/error/missing/invalid).</summary>
public record StepFinished(
    Guid RunId,
    string Uid,
    string StepId,
    string Status,
    long DurationMs,
    string? ErrorMessage) : MonitorEvent(RunId);

/// <summary>
/// A supervisor lane's worker was handed a set of tests (issue #84). Fired for the first pass
/// and again for a same-process retry, which goes back to the lane the test ran in — so a lane
/// can start more than once, each time with the uids it was handed. Isolated and recycled runs
/// are not lanes: they are one-test processes and never announce one.
/// </summary>
public record LaneStarted(
    Guid RunId,
    int Lane,
    IReadOnlyList<string> Uids,
    DateTimeOffset At) : MonitorEvent(RunId);

/// <summary>
/// The lane's worker finished the set it was given. Crashed mirrors WorkerRunResult.Crashed — the
/// account of the crash (exit code, last standard error) travels on <see cref="WorkerFaulted"/>.
/// Outcomes is how many results the worker reported, which matters for a foreign-framework
/// worker (xUnit, tUnit) that streams no scenario events of its own.
/// </summary>
public record LaneFinished(
    Guid RunId,
    int Lane,
    int Outcomes,
    bool Crashed,
    DateTimeOffset At) : MonitorEvent(RunId);

/// <summary>A supervisor-owned resource was thrown away and stood up again before a retry.</summary>
public record ResourceRecycled(
    Guid RunId,
    string Resource,
    DateTimeOffset At) : MonitorEvent(RunId);

/// <summary>
/// A worker process died, with the account of it a person needs at 2am: the lane it was running
/// (null for a one-test isolated or recycled process), the exit code when it had exited by the
/// time the supervisor looked, and the tail of its standard error. Fault is the human-readable
/// sentence SupervisorResults.WorkerFaults collects, so the dashboard and the report agree.
/// </summary>
public record WorkerFaulted(
    Guid RunId,
    int? Lane,
    string Fault,
    int? ExitCode,
    string? StandardError,
    DateTimeOffset At) : MonitorEvent(RunId);
