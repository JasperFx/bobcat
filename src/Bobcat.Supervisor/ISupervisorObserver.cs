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
}
