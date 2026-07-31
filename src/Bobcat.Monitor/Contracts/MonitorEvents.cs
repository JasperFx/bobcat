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
    int? TotalScenarios) : MonitorEvent(RunId);

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
