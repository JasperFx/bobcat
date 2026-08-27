using Bobcat.Resilience;

namespace Bobcat.Supervisor;

/// <summary>
/// Live notification of what the supervisor is doing, as it does it — issue #84.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SupervisorResults"/> reports all of this at the end. That is too late for anything
/// watching a run: retry topology, lane occupancy and worker deaths are exactly the facts a
/// person stares at a dashboard <em>during</em> a long run to see, and the supervisor is the only
/// thing in the system that knows them. A worker knows it was asked to run some tests; it does
/// not know it is a replacement, which lane it is, or that the broker was thrown away before it
/// started.
/// </para>
/// <para>
/// Every member is a default no-op, so a consumer implements only the callbacks it wants and
/// gains nothing to maintain when new ones are added.
/// </para>
/// <para>
/// <strong>An observer must never change what a run does.</strong> Callbacks are invoked
/// synchronously on the supervisor's own thread and an exception from one is caught and logged
/// rather than propagated — the same contract the monitor publisher has always had. Anything
/// slow belongs behind a queue in the implementation.
/// </para>
/// </remarks>
public interface ISupervisorObserver
{
    /// <summary>One attempt at one test finished, and the policy has decided what happens next.</summary>
    /// <remarks>
    /// Fired for every attempt including the first, and including passes: "which tests are
    /// running and how are they going" needs the passes too.
    /// </remarks>
    void AttemptRecorded(string uid, SupervisorAttempt attempt)
    {
    }

    /// <summary>
    /// A retry is about to happen, with the disposition and reason the policy gave for it.
    /// </summary>
    /// <remarks>
    /// Fired when the retry is scheduled — after the budget and the resolve step have had their
    /// say, so a disposition that was requested but not honoured never reaches here. This is the
    /// information the supervisor uniquely has: a worker running the retry cannot know it is one.
    /// </remarks>
    void RetryScheduled(string uid, int nextAttempt, Disposition disposition)
    {
    }

    /// <summary>
    /// A worker was launched, with the launch it was for — lane, purpose, and, when the client
    /// drives a separate process, its process id (issue #146). Fired for every launch,
    /// discovery included: a discovery worker never reports test progress, but it is still a
    /// process someone may need to diagnose.
    /// </summary>
    /// <remarks>
    /// This is where lane-to-pid correlation starts. <see cref="TestUpdated"/> carries the same
    /// context, but only once a test reports — and only from clients that can observe their
    /// worker mid-run.
    /// </remarks>
    void WorkerStarted(WorkerLaunchContext worker)
    {
    }

    /// <summary>A lane's worker was handed a set of tests.</summary>
    void LaneStarted(int lane, IReadOnlyList<string> uids)
    {
    }

    /// <summary>A lane's worker finished the set it was given.</summary>
    void LaneFinished(int lane, WorkerRunResult result)
    {
    }

    /// <summary>A registered resource was thrown away and stood up again.</summary>
    void ResourceRecycled(string name)
    {
    }

    /// <summary>
    /// A worker died, with the account of it — exit code and last standard error — that
    /// <see cref="SupervisorResults.WorkerFaults"/> collects.
    /// </summary>
    void WorkerFaulted(string fault)
    {
    }

    /// <summary>
    /// A worker reported a test's live state mid-run — "in progress" when it starts, then its
    /// verdict — with which launch (lane and purpose) the worker was. Issue #99's tap on MTP's
    /// <c>testing/testUpdates/tests</c>: before this, the supervisor learned what a lane was
    /// doing only when the whole lane finished.
    /// </summary>
    /// <remarks>
    /// Fired from the worker client's I/O thread, so it can interleave with the other callbacks.
    /// The terminal update precedes <see cref="AttemptRecorded"/> for the same test and carries
    /// no policy verdict — that is what <see cref="AttemptRecorded"/> is for. A client that
    /// cannot observe its worker mid-run (see <see cref="IWorkerClient.OnTestUpdate"/>) never
    /// fires this at all, so a consumer must not rely on it for correctness.
    /// </remarks>
    void TestUpdated(WorkerLaunchContext worker, WorkerTestUpdate update)
    {
    }

    /// <summary>
    /// A test has been in flight longer than its stall threshold allows (issue #145). The name
    /// is the value: a hung batch's log currently cannot say which test wedged, and the CI cap
    /// that eventually fires takes the answer with it.
    /// </summary>
    /// <remarks>
    /// Fired once per attempt, from the supervisor's own timer thread, when the threshold is
    /// crossed — the heartbeat's climbing longest-running figure is the continuous view.
    /// Detection always reports; whether the supervisor then acts is
    /// <see cref="Supervisor.StallAction"/>'s opt-in decision (issue #173), and by default it
    /// never kills a worker over a stall.
    /// </remarks>
    void TestStalled(WorkerLaunchContext worker, string uid, string displayName, TimeSpan inFlight)
    {
    }

    /// <summary>
    /// The supervisor is killing a worker to clear a stalled test (issue #173) — fired only
    /// when <see cref="Supervisor.StallAction"/> is <see cref="StallAction.KillAndRetry"/>,
    /// after the stall itself was announced through
    /// <see cref="TestStalled(WorkerLaunchContext,string,string,TimeSpan)"/>. Default no-op.
    /// </summary>
    void StallKilled(StallKill kill)
    {
    }

    /// <summary>
    /// The periodic progress view while a run is in flight (issue #148), on the interval
    /// <c>Supervisor.HeartbeatInterval</c> asked for. Fired from the supervisor's timer thread.
    /// </summary>
    void Heartbeat(SupervisorHeartbeat heartbeat)
    {
    }

    /// <summary>
    /// The same death, structured: which lane (null for a one-test isolated or recycled
    /// process), the exit code and the standard error tail as separate facts, alongside the
    /// sentence. The supervisor calls this one; by default it forwards to
    /// <see cref="WorkerFaulted(string)"/>, so an observer keeps working whichever it implements.
    /// </summary>
    void WorkerFaulted(WorkerFault fault) => WorkerFaulted(fault.Description);
}

/// <summary>
/// A worker's death as the supervisor saw it. <see cref="Description"/> is the sentence
/// <see cref="SupervisorResults.WorkerFaults"/> collects; the other members are the facts it was
/// written from, kept separate so a dashboard can render them as more than prose.
/// </summary>
/// <param name="Lane">The lane whose worker died; null when the process ran one test alone.</param>
/// <param name="ProcessId">
/// The dead worker's OS process id, when the client drove one — so a post-mortem can name the
/// process rather than describe it (issue #146).
/// </param>
public sealed record WorkerFault(
    string Description, int? ExitCode, string? StandardError, int? Lane, int? ProcessId = null);
