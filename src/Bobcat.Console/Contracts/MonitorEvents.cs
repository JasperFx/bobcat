using JasperFx.Testing;
using System.Text.Json.Serialization;
using Wolverine;

namespace Bobcat.Console.Contracts;

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
[JsonDerivedType(typeof(StepProgress), "step_progress")]
[JsonDerivedType(typeof(LaneStarted), "lane_started")]
[JsonDerivedType(typeof(LaneFinished), "lane_finished")]
[JsonDerivedType(typeof(ResourceRecycled), "resource_recycled")]
[JsonDerivedType(typeof(WorkerFaulted), "worker_faulted")]
[JsonDerivedType(typeof(WorkerStarted), "worker_started")]
[JsonDerivedType(typeof(TestStalled), "test_stalled")]
[JsonDerivedType(typeof(TestStarted), "test_started")]
[JsonDerivedType(typeof(TestFinished), "test_finished")]
[JsonDerivedType(typeof(RunProgress), "run_progress")]
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

/// <summary>
/// Attempt is 1-based; a value above 1 marks a retry attempt. TotalSteps is how many steps
/// this attempt will run — the plan is built before the scenario is announced, so it is a fact
/// rather than an estimate; null from a publisher that predates it.
/// </summary>
public record ScenarioStarted(
    Guid RunId,
    string Uid,
    string Feature,
    string Scenario,
    int Attempt,
    DateTimeOffset At,
    int? TotalSteps = null) : MonitorEvent(RunId);

/// <summary>
/// Outcome mirrors RunOutcome: CleanPass / PassOnRetry / Failed / Aborted. Uid is the spec
/// identity {Feature}/{Scenario} — the Identity a design-time SpecificationDescriptor carries
/// (issue #106), so run evidence joins the Event Model without a mapping table. TouchedTypes is
/// the run evidence itself (issue #107): CLR types the scenario observably touched, in
/// first-touch order, deduplicated — observed, never asserted; null from an older publisher or
/// a scenario that recorded nothing. At stamps when the scenario finished, so a consumer can
/// age the evidence; both are optional and additive.
/// </summary>
public record ScenarioFinished(
    Guid RunId,
    string Uid,
    string Outcome,
    int Attempts,
    long DurationMs,
    string? ErrorMessage,
    IReadOnlyList<TouchedType>? TouchedTypes = null,
    DateTimeOffset? At = null) : MonitorEvent(RunId);

/// <summary>
/// A CLR type a scenario touched, on the wire (issue #107). Deliberately the same three fields
/// as JasperFx's <c>TypeDescriptor</c> — <c>FullName</c> is the join key against the resolved
/// types a design-time <c>SpecificationDescriptor</c> carries — but mirrored here rather than
/// referenced, because this file is a dependency-free copy of the wire contract.
/// </summary>
public record TouchedType(string Name, string FullName, string AssemblyName);

/// <summary>Disposition mirrors DispositionKind; Reason is the human-readable policy reason.</summary>
public record RetryScheduled(
    Guid RunId,
    string Uid,
    int NextAttempt,
    string Disposition,
    string Reason) : MonitorEvent(RunId);

/// <summary>
/// Kind mirrors StepKind (Given/When/Then/...). StepNumber is the step's 1-based position in
/// the attempt and TotalSteps the attempt's count, so "step 3 of 9" needs no counting of
/// events a watcher may have missed; ScenarioElapsedMs is how far into the scenario's wall
/// clock the step started. All three are null from an older publisher.
/// </summary>
public record StepStarted(
    Guid RunId,
    string Uid,
    string StepId,
    string Kind,
    string Text,
    int? StepNumber = null,
    int? TotalSteps = null,
    long? ScenarioElapsedMs = null) : MonitorEvent(RunId);

/// <summary>
/// Status mirrors ResultStatus (ok/success/failed/error/missing/invalid). ScenarioElapsedMs is
/// how far into the scenario's wall clock the step finished; null from an older publisher.
/// </summary>
public record StepFinished(
    Guid RunId,
    string Uid,
    string StepId,
    string Status,
    long DurationMs,
    string? ErrorMessage,
    long? ScenarioElapsedMs = null) : MonitorEvent(RunId);

/// <summary>
/// Interim progress from a step that is still running — the wire form of Bobcat's
/// <c>IExecutionObserver.StepProgress</c>. Two shapes share one event: a <c>[TableGrammar]</c>
/// ticking through its rows (Row/TotalRows set, no Message) and a <c>[WaitFor]</c> poll loop
/// reporting what it last saw (Message set, no rows). ElapsedMs is time since the step
/// started. Upserted per step by every consumer — only the latest matters.
/// </summary>
public record StepProgress(
    Guid RunId,
    string Uid,
    string StepId,
    string? Message,
    int? Row,
    int? TotalRows,
    long ElapsedMs) : MonitorEvent(RunId);

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

/// <summary>
/// A worker process was launched (issue #146): its purpose (Lane / Isolated / Recycled — a
/// discovery worker is never announced, because it launches before the run bracket opens and
/// run_started stays the stream's first event), the lane when it is one (null otherwise, same
/// rule as WorkerFaulted), and its OS pid when the client drives a separate process. This is
/// where lane-to-pid correlation starts — the fact an external diagnostic (a dump, an RSS
/// sampler) must be pointed at, which consumers previously had to guess from /proc.
/// </summary>
public record WorkerStarted(
    Guid RunId,
    int? Lane,
    string Purpose,
    int? ProcessId,
    DateTimeOffset At) : MonitorEvent(RunId);

/// <summary>
/// A test crossed its stall threshold (issue #145) — in flight longer than its budget allows.
/// Fired once per attempt; the run_progress stream's longest-running clause is the continuous
/// view. The name is the value: a hung batch's log cannot say which test wedged, and the CI
/// cap that eventually fires takes the answer with it. InFlightMs is how long the test had
/// been running at detection.
/// </summary>
public record TestStalled(
    Guid RunId,
    string Uid,
    string DisplayName,
    long InFlightMs,
    int? Lane,
    int? ProcessId,
    DateTimeOffset At) : MonitorEvent(RunId);

/// <summary>
/// The supervisor's opt-in progress heartbeat (issue #148) — distinct from run_heartbeat,
/// which is a bare liveness ping. Only posted when the supervisor's HeartbeatInterval is
/// configured. For a foreign-framework worker (xUnit, tUnit) that streams no scenario events,
/// this is the only live progress a run has. The longest-running trio is null when nothing is
/// in flight; PeakWorkerRssBytes is the highest worker RSS seen so far when memory sampling
/// (issue #149) is on, and null otherwise — unmeasured is never zero.
/// </summary>
public record RunProgress(
    Guid RunId,
    long ElapsedMs,
    int Completed,
    int Total,
    int InFlight,
    string? LongestRunningUid,
    string? LongestRunningDisplayName,
    long? LongestRunningMs,
    long? PeakWorkerRssBytes,
    DateTimeOffset At) : MonitorEvent(RunId);

/// <summary>
/// One foreign test started (issue #195) — the supervisor's forwarding of a worker's live
/// <c>testing/testUpdates/tests</c> stream, which is the only per-test progress a run whose
/// worker is not itself a Bobcat runner ever produces. A Bobcat worker publishes its own
/// richer scenario/step stream, and the console's fold lets that stream win for any uid it
/// touches, so the two never fight over the same test.
/// </summary>
/// <remarks>
/// <c>Uid</c> here is the WORKER's test id, not the <c>{Feature}/{Scenario}</c> spec identity
/// <see cref="ScenarioStarted"/> carries. For a Bobcat worker they are the same string; for an
/// xUnit or tUnit worker it is a dotted method name that corresponds to no
/// <c>SpecificationDescriptor</c> — which is why the run evidence #106/#107 join on lives on
/// the scenario events and not here. <c>Lane</c> is null for a one-test isolated or recycled
/// process, the same rule as <see cref="WorkerFaulted"/>.
/// </remarks>
public record TestStarted(
    Guid RunId,
    string Uid,
    string DisplayName,
    int? Lane,
    DateTimeOffset At) : MonitorEvent(RunId);

/// <summary>
/// One foreign test reached a verdict (issue #195). <c>State</c> is the framework's own word —
/// Passed / Failed / Error / Skipped / Timeout / Cancelled — deliberately NOT re-labelled into
/// Bobcat's RunOutcome vocabulary by the publisher: two enums meaning the same thing is how a
/// vocabulary drifts, and the console does the mapping in one documented place per side.
/// Indeterminate never travels here: silence is not a verdict, and a padded outcome is not a
/// live one.
/// </summary>
/// <param name="DurationMs">
/// How long the test was in flight, measured from its own in-progress update. Null when the
/// supervisor never saw that update (it attached mid-test, or the worker never sent one) —
/// unmeasured is never zero.
/// </param>
public record TestFinished(
    Guid RunId,
    string Uid,
    string DisplayName,
    string State,
    long? DurationMs,
    int? Lane,
    DateTimeOffset At) : MonitorEvent(RunId);
