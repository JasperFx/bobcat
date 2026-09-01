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
[JsonDerivedType(typeof(WorkerStarted), "worker_started")]
[JsonDerivedType(typeof(TestStalled), "test_stalled")]
[JsonDerivedType(typeof(TestStarted), "test_started")]
[JsonDerivedType(typeof(TestFinished), "test_finished")]
[JsonDerivedType(typeof(RunProgress), "run_progress")]
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
    // The spec identity {Feature}/{Scenario} — the same string SpecNodeMapping.Uid produces and
    // the retry budget keys on, and the Identity a design-time SpecificationDescriptor carries
    // (issue #106) — so run evidence joins the Event Model without a mapping table.
    string Uid,
    string Outcome,
    int Attempts,
    long DurationMs,
    string? ErrorMessage,
    // CLR types the scenario observably touched — commands dispatched, events appended or
    // emitted, aggregates arranged, messages sent, read models loaded — in first-touch order,
    // deduplicated (issue #107). Observed, never asserted; null from an older publisher or a
    // scenario that recorded nothing. Optional and additive.
    IReadOnlyList<TouchedType>? TouchedTypes = null,
    // When the scenario finished — the stamp a consumer uses to age this evidence. Null from an
    // older publisher. Optional and additive.
    DateTimeOffset? At = null) : MonitorEvent(RunId);

/// <summary>
/// A CLR type a scenario touched, on the wire (issue #107). Deliberately the same three fields
/// as JasperFx's <c>TypeDescriptor</c> — <c>FullName</c> is the join key against the resolved
/// types a design-time <c>SpecificationDescriptor</c> carries — but mirrored here rather than
/// referenced, because this file is a dependency-free copy of the wire contract.
/// </summary>
public record TouchedType(string Name, string FullName, string AssemblyName);

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

// The supervisor-observability cluster's live surfaces (issues #145/#146/#148/#149) — posted
// by SupervisorRunPublisher from the same observer callbacks a code consumer gets.

public record WorkerStarted(
    Guid RunId,
    // Null for a one-test isolated/recycled process — same rule as WorkerFaulted's lane.
    int? Lane,
    // Mirrors WorkerPurpose: Lane / Isolated / Recycled. Discovery is never announced on the
    // wire — it launches before the run bracket opens, and run_started stays first.
    string Purpose,
    // The worker's OS pid, when the client drives a separate process (issue #146) — what an
    // external diagnostic must be pointed at.
    int? ProcessId,
    DateTimeOffset At) : MonitorEvent(RunId);

public record TestStalled(
    Guid RunId,
    string Uid,
    string DisplayName,
    // How long the test had been in flight when it crossed its threshold. Fired once per
    // attempt (issue #145); the run_progress stream is the continuous view.
    long InFlightMs,
    int? Lane,
    int? ProcessId,
    DateTimeOffset At) : MonitorEvent(RunId);

// The supervisor's opt-in progress heartbeat (issue #148) — distinct from run_heartbeat, which
// is a bare liveness ping. Only posted when the supervisor's HeartbeatInterval is configured.
public record RunProgress(
    Guid RunId,
    long ElapsedMs,
    int Completed,
    int Total,
    int InFlight,
    // The longest-running in-flight test — the clause a reader watches: a stuck run shows as
    // this figure climbing. All three null when nothing is in flight.
    string? LongestRunningUid,
    string? LongestRunningDisplayName,
    long? LongestRunningMs,
    // Highest worker RSS seen so far, when memory sampling (issue #149) is on; null otherwise —
    // unmeasured is never zero.
    long? PeakWorkerRssBytes,
    DateTimeOffset At) : MonitorEvent(RunId);

// The supervisor's forwarding of a worker's live per-test stream (issue #195) — the only
// per-test progress a run whose worker is not itself a Bobcat runner ever produces. Uid is
// the WORKER's test id, not the {Feature}/{Scenario} spec identity ScenarioStarted carries;
// for a foreign worker it corresponds to no SpecificationDescriptor, which is why the run
// evidence join stays on the scenario events. Lane is null for a one-test process.

public record TestStarted(
    Guid RunId,
    string Uid,
    string DisplayName,
    int? Lane,
    DateTimeOffset At) : MonitorEvent(RunId);

// State is the framework's own word — Passed / Failed / Error / Skipped / Timeout / Cancelled
// — never re-labelled into RunOutcome by the publisher. Indeterminate never travels here:
// silence is not a verdict. DurationMs is null when no in-progress update was seen to measure
// from — unmeasured is never zero.
public record TestFinished(
    Guid RunId,
    string Uid,
    string DisplayName,
    string State,
    long? DurationMs,
    int? Lane,
    DateTimeOffset At) : MonitorEvent(RunId);
