using JasperFx.Testing;
using System.Text.Json.Serialization;
using Wolverine;

namespace Bobcat.Ui.Contracts;

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
    string? Tag = null,
    // The Claude Code session that started the run — Bobcat reads CLAUDE_CODE_SESSION_ID
    // (bobcat#389). Opaque like the tag, and independent of it: the tag says what work the run
    // speaks for, the session says who ran it. The strongest link from a run to an agent
    // (stoat#76). Null from a publisher that predates it, or a run nothing launched from a session.
    string? Session = null,
    // The monitor command that caused the run (JasperFx/bobcat#392): a resident runner stamps the
    // command id it acknowledged, so the button that asked can follow its own run (stoat#77).
    // Opaque like the tag and the session, and independent of both. Null for every other run.
    string? Command = null) : MonitorEvent(RunId);

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
/// rather than an estimate; null from a publisher that predates it. For a projected test with no
/// marker comments the publisher falls back to the plan's count (stoat#59).
/// </summary>
public record ScenarioStarted(
    Guid RunId,
    string Uid,
    string Feature,
    string Scenario,
    int Attempt,
    DateTimeOffset At,
    int? TotalSteps = null,
    // What the scenario SAYS it does, in order (issue #304) — a marker-comment test's narrative,
    // known at compile time. It arrives with the announcement rather than as steps because a
    // declared step has not run, and StepStarted is the event that says something did. Null from
    // an older publisher and from a scenario that declares nothing.
    IReadOnlyList<DeclaredStepInfo>? DeclaredSteps = null,
    // Every step the scenario is GOING to take, in source order (stoat#59) — a projected test's
    // [Given]/[When]/[Then]/[Step] calls, read off the syntax tree at compile time. It is what lets
    // a viewer render the steps an aborted scenario never reached instead of letting them vanish.
    // Null from an older publisher and from a scenario with no plan behind it (every .feature
    // scenario today).
    IReadOnlyList<PlannedStepInfo>? PlannedSteps = null,
    // The method's spec identity when this scenario is one row of a data-driven specification
    // (bobcat#569): Uid is then the row's own identity, and this is the {Feature}/{Scenario} the
    // Event Model knows, so rows fold under it without parsing Uid. Null for a scenario that is
    // not a row, and from an older publisher.
    string? ParentSpecId = null) : MonitorEvent(RunId);

/// <summary>
/// One step a scenario plans to take, on the wire (stoat#59). Keyword is Given / When / Then, or
/// EMPTY for a step that spells no keyword — rendered as no label, never as a default. Template is
/// the step text with its placeholders UNRESOLVED: at plan time no argument has been evaluated, and
/// filling them in would describe a run that did not happen. Grammar is the bound step method as
/// <c>Type.Method</c>; DeclaredStepNumber is the 1-based marker comment the call sits under, or null.
/// </summary>
public record PlannedStepInfo(string Keyword, string Template, string Grammar, int? DeclaredStepNumber = null);

/// <summary>
/// One declared step on the wire (issue #304): a marker comment's keyword and sentence, with no
/// status, duration or id — what ran attaches to it through StepStarted.DeclaredStepNumber.
/// </summary>
public record DeclaredStepInfo(string Keyword, string Text);

/// <summary>
/// Outcome mirrors RunOutcome: CleanPass / PassOnRetry / Failed / Aborted. Uid is the spec
/// identity {Feature}/{Scenario} — the Identity a design-time SpecificationDescriptor carries
/// (issue #106), so run evidence joins the Event Model without a mapping table. TouchedTypes is
/// the run evidence itself (issue #107): CLR types the scenario observably touched, in
/// first-touch order, deduplicated — observed, never asserted; null from an older publisher or
/// a scenario that recorded nothing. At stamps when the scenario finished, so a consumer can
/// age the evidence; both are optional and additive. Reports (bobcat#408, Bobcat 0.37.0) are the
/// scenario's own accounts of what its system did, as cell grids rather than any one step's claim,
/// already filtered by visibility — so a passing scenario carries none unless the run was verbose.
/// StepsMs (bobcat#477, Bobcat 0.50.0) is what the scenario's top-level steps cost, summed, next to
/// DurationMs, which is the whole bracket; the difference is time no step owns. The projected lane
/// sends it, and null means "not measured", never zero. ParentSpecId (bobcat#569) names the
/// method's identity when this scenario is one row of a data-driven specification. Attachments
/// (bobcat#571) are references to the failure evidence the scenario kept — name, media type, size,
/// path, step — never the bytes, latched like Reports; SuppressedAttachments counts the ones past
/// the per-scenario cap.
/// </summary>
public record ScenarioFinished(
    Guid RunId,
    string Uid,
    string Outcome,
    int Attempts,
    long DurationMs,
    string? ErrorMessage,
    IReadOnlyList<TouchedType>? TouchedTypes = null,
    DateTimeOffset? At = null,
    IReadOnlyList<ScenarioReportInfo>? Reports = null,
    long? StepsMs = null,
    // The method's spec identity when this scenario is one row of a data-driven specification
    // (bobcat#569), as on ScenarioStarted. Null for a scenario that is not a row.
    string? ParentSpecId = null,
    IReadOnlyList<StepLogInfo>? Logs = null,
    IReadOnlyList<ScenarioAttachmentInfo>? Attachments = null,
    int? SuppressedAttachments = null) : MonitorEvent(RunId);

/// <summary>
/// The log lines one step wrote through <c>SpecLog.Write</c> (bobcat#547), or the scenario's own
/// lines when <see cref="StepId"/> is null. <see cref="Suppressed"/> counts the lines past the
/// per-step cap that were not kept, so a consumer says "…and N more" rather than truncating
/// silently.
/// </summary>
public record StepLogInfo(string? StepId, IReadOnlyList<string> Lines, int Suppressed);

/// <summary>
/// A reference to one attachment (bobcat#571): Path is the file beside the run's results file, null
/// when it went to the test platform instead; StepId is the step it hangs on, null for the scenario;
/// Omitted says why the bytes were not kept (over the size cap).
/// </summary>
public record ScenarioAttachmentInfo(
    string Name,
    string MediaType,
    long Size,
    string? Path = null,
    string? StepId = null,
    string? Omitted = null);

/// <summary>
/// One scenario report on the wire (bobcat#408): a titled grid describing what the scenario's
/// system actually did, as opposed to whether it matched. It carries <see cref="StepCell"/>, not a
/// sibling cell record — one cell shape on this wire, so an informational cell arrives with Value
/// set. Columns gives the grid its order (cells place themselves by RowIndex against it), and
/// SuppressedRows is how many rows were dropped: zero means none, and a truncation a reader cannot
/// see is a report that misleads.
/// </summary>
public record ScenarioReportInfo(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<StepCell> Cells,
    string? ShortTitle = null,
    int SuppressedRows = 0);

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
/// Kind mirrors StepKind (Given/When/Then/...) and is free-form: <c>""</c> is a step that spells no
/// keyword (a <c>[Step]</c> grammar) and <c>"And"</c> a repeat of the previous keyword, decided by
/// the recorder (stoat#59). StepNumber is the step's 1-based position in
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
    long? ScenarioElapsedMs = null,
    // 1-based index into ScenarioStarted.DeclaredSteps of the marker comment this step ran
    // inside (issue #304), decided by the generator from the call site's line. Null when the
    // step belongs to no declared region.
    int? DeclaredStepNumber = null,
    // 1-based index into ScenarioStarted.PlannedSteps of the call site this step came from
    // (stoat#59), so which planned steps were reached is a set difference rather than a guess.
    // Matching is by call-site ordinal, never by text, so it survives a helper called twice, in a
    // loop, or behind an `if`. Null for a step with no plan behind it.
    int? PlannedStepNumber = null,
    // Where the INPUT values sit in Text (stoat#59). Travels rather than being recomputed: searching
    // the sentence for a value marks the wrong characters whenever it also occurs in the prose —
    // "3 of them cost 3" would italicise the first 3. Null when nothing was substituted.
    IReadOnlyList<StepValueSpan>? Values = null) : MonitorEvent(RunId);

/// <summary>Where one substituted input value sits in a step's rendered sentence (stoat#59).</summary>
public record StepValueSpan(int Start, int Length);

/// <summary>
/// A step's own report, at the end of it — everything Storyteller put on a specification line.
/// Status mirrors ResultStatus (ok/success/failed/error/missing/invalid). ScenarioElapsedMs is
/// how far into the scenario's wall clock the step finished; null from an older publisher.
/// </summary>
/// <remarks>
/// <para>
/// Everything after ScenarioElapsedMs is optional and additive, and <b>null rather than empty</b>
/// when the step had nothing to say: an empty list would claim the step was asked and had nothing,
/// which is a different fact from not being asked.
/// </para>
/// <para>
/// <b>Cells</b> (stoat#58) are the named expected/actual comparisons, each judged on its own — a
/// wrong product must not hide a right sum. <b>Columns</b> is the column order for a step whose cells
/// form a table (set verification, decision table); a grid is reassembled from it and
/// <see cref="StepCell.RowIndex"/>.
/// </para>
/// <para>
/// <b>ExceptionType / StackTrace</b> (stoat#60) are null for an assertion that merely disagreed —
/// nothing was thrown, so there is no stack — and set when the step ended in an exception. A viewer
/// relies on that rather than sniffing the message. <b>StackFrames</b> is the same stack with
/// Bobcat's own plumbing, the test runner's and the reflection/async machinery removed by Bobcat's
/// <c>SpecStackTrace</c> rule, and <b>HiddenStackFrames</b> says how many went: an edited stack a
/// reader cannot tell was edited is worse than a noisy one. The rule is Bobcat's knowledge, so it
/// travels rather than being reimplemented here; the raw StackTrace stays for "show every frame".
/// </para>
/// </remarks>
public record StepFinished(
    Guid RunId,
    string Uid,
    string StepId,
    string Status,
    long DurationMs,
    string? ErrorMessage,
    long? ScenarioElapsedMs = null,
    IReadOnlyList<StepCell>? Cells = null,
    IReadOnlyList<string>? Columns = null,
    // What the step logged, in order — Storyteller's Context.Reporting.Log.
    IReadOnlyList<string>? Logs = null,
    // Named diagnostics the step attached.
    IReadOnlyDictionary<string, string>? Diagnostics = null,
    string? ExceptionType = null,
    string? StackTrace = null,
    IReadOnlyList<string>? StackFrames = null,
    int HiddenStackFrames = 0) : MonitorEvent(RunId);

/// <summary>
/// One value comparison a step reported — Storyteller's expected/actual cell (stoat#58). Status is
/// the framework's own word (<c>success</c> / <c>failed</c> / <c>error</c> / <c>ok</c>), verbatim and
/// never re-labelled by the publisher. RowIndex is -1 for a cell in an ordinary sentence and the
/// 0-based row for one that belongs to a table.
/// </summary>
/// <param name="Value">The plain text of a cell nothing JUDGED (bobcat#396, Bobcat 0.30.0): a decision
/// table's input column, an echoed value. Null whenever Expected, Actual or Note says something —
/// and deliberately not folded into Actual, because a cell with neither is how a report knows an
/// input column earns no verdict. Missing from this mirror until Bobcat 0.30.0, so every literal
/// reached the grid empty.</param>
/// <param name="Comparison">What a judged cell compared, as one word from Bobcat's closed set
/// (bobcat#384, Bobcat 0.37.0): <c>equals</c>, <c>notEquals</c>, <c>greaterThan</c>,
/// <c>greaterThanOrEqual</c>, <c>lessThan</c>, <c>lessThanOrEqual</c>, <c>contains</c>,
/// <c>startsWith</c>, <c>endsWith</c>, <c>approximately</c>, <c>isNull</c>, <c>isNotNull</c>,
/// <c>isEmpty</c>, <c>isNotEmpty</c>. <b>Null means equality</b> — what every publisher said before
/// the field existed. Not a fifth content field: it says how Expected and Actual RELATE.</param>
public record StepCell(
    string Name,
    string Status,
    string? Expected = null,
    string? Actual = null,
    string? Note = null,
    int RowIndex = -1,
    string? Value = null,
    string? Comparison = null);

/// <summary>
/// Interim progress from a step that is still running — the wire form of Bobcat's
/// <c>IExecutionObserver.StepProgress</c>. Two shapes share one event: a <c>[TableGrammar]</c>
/// ticking through its rows (Row/TotalRows set, no Message) and a <c>[WaitFor]</c> poll loop
/// reporting what it last saw (Message set, no rows). ElapsedMs is time since the step
/// started. Upserted per step by every consumer — only the latest matters.
/// </summary>
/// <remarks>
/// <b>Cells</b> (stoat#62) are the comparisons the step has made SO FAR — the whole set each time,
/// not a delta, so "latest wins" still holds and a dropped update costs nothing. It is what lets a
/// step that spends thirty seconds making twenty comparisons fill in cell by cell instead of
/// showing nothing until it ends. Null from a publisher that does not send them, which today is
/// every publisher: the Stoat side is built first, as the pull for Bobcat's.
/// </remarks>
public record StepProgress(
    Guid RunId,
    string Uid,
    string StepId,
    string? Message,
    int? Row,
    int? TotalRows,
    long ElapsedMs,
    IReadOnlyList<StepCell>? Cells = null) : MonitorEvent(RunId);

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
    DateTimeOffset At,
    // The {Feature}/{Scenario} spec identity when the supervisor could name the test from the
    // suite's spec manifest (bobcat#515), null otherwise. Carried, not yet folded on.
    string? SpecId = null) : MonitorEvent(RunId);

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
    DateTimeOffset At,
    // As on TestStarted (bobcat#515).
    string? SpecId = null) : MonitorEvent(RunId);
