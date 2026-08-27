namespace Bobcat.Supervisor;

/// <summary>
/// What the supervisor does when a test crosses its stall threshold (issue #173) — the
/// "second, separable, opt-in decision" the #145 stall detection deliberately deferred.
/// </summary>
/// <remarks>
/// <para>
/// Off (<see cref="Report"/>) by default, so the "a threshold turned into an automatic kill
/// will eventually fire on a legitimately slow integration test" hazard stays a choice the
/// operator makes, never a default they inherit. Whatever the action, detection still reports:
/// the stall is logged, announced to observers, and collected on
/// <see cref="SupervisorResults.StalledTests"/> exactly as before.
/// </para>
/// <para>
/// The consumer case that promoted this from deferral to feature: CritterWatch's
/// <c>EventStoreExplorerTests</c> spun for 49 minutes on a wedged container fleet that a fresh
/// fleet ran in 7½ — an environmental wedge that read as a wall of product regressions, in a
/// codebase whose house notes carried the diagnosis as folklore because nothing automated it.
/// </para>
/// </remarks>
public enum StallAction
{
    /// <summary>
    /// Name the stalled test and keep going — #145's behaviour, and the default. Report,
    /// don't act.
    /// </summary>
    Report,

    /// <summary>
    /// Kill the stalled worker and retry the stalled test alone in a fresh process, once.
    /// Batch-mates killed alongside are resumed in their lane — they were innocent, and turning
    /// them into indeterminate results would punish them for the supervisor's own kill. A test
    /// that stalls again on its own retry is failed rather than retried forever, and once the
    /// run's <see cref="Supervisor.MaxStallKills"/> ceiling is spent the next stall aborts the
    /// run — repeated stalls across tests are the shape of dead infrastructure, not a flaky
    /// test. Stall kills ride their own count, never the <see cref="Bobcat.Resilience.RetryBudget"/>
    /// and never the flakiness ledger: a wedge is not a flake, and conflating them corrupts both.
    /// </summary>
    KillAndRetry,

    /// <summary>
    /// Stop the whole run on the first stall, naming the test, the lane and the pid in
    /// <see cref="SupervisorResults.AbortReason"/>. For the wedge that is not test-shaped —
    /// the daemon-is-dead case, where retrying one test at a time just burns the rest of the
    /// budget producing misleading failures.
    /// </summary>
    AbortRun
}
