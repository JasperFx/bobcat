using JasperFx.Testing;
using Bobcat.Resilience;

namespace Bobcat.Supervisor;

/// <summary>Where an attempt was executed. Reported, because it is what the user asked for.</summary>
public enum AttemptPlacement
{
    /// <summary>Ran alongside other tests in a shared worker.</summary>
    Batched,

    /// <summary>Re-run in the same worker process as the previous attempt.</summary>
    SameProcess,

    /// <summary>Ran alone in a process of its own.</summary>
    IsolatedProcess,

    /// <summary>Ran alone in a fresh process, after the named resources were recycled.</summary>
    RecycledProcess
}

/// <summary>One attempt at one test, and what the supervisor decided afterwards.</summary>
public sealed record SupervisorAttempt(
    int AttemptNumber,
    WorkerOutcome Outcome,
    AttemptPlacement Placement,
    Disposition Disposition)
{
    /// <summary>Set when a disposition could not be acted on, with the reason.</summary>
    public string? Unsupported { get; init; }

    /// <summary>
    /// True when this attempt's outcome was manufactured by the supervisor's own stall kill
    /// (issue #173) — the stalled test's killed attempt, or a batch-mate's that died alongside
    /// it. A wedge is not a flake: attempts marked this way never make a test "passed on
    /// retry" and never put it in <see cref="SupervisorResults.Quarantine"/>, because
    /// conflating the two corrupts the flakiness ledger in both directions.
    /// </summary>
    public bool StallInduced { get; init; }

    public bool Succeeded => Outcome.Succeeded;
}

/// <summary>Everything the run learned about one test.</summary>
public sealed class TestReport
{
    public required string Uid { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<SupervisorAttempt> Attempts { get; init; }

    public SupervisorAttempt Final => Attempts[^1];
    public int AttemptCount => Attempts.Count;
    public bool WasRetried => Attempts.Count > 1;

    /// <summary>
    /// True when some attempt beyond the first was caused by the test's own failure, as opposed
    /// to every extra attempt being the supervisor killing a stalled worker out from under it
    /// (issue #173). This — not the structural <see cref="WasRetried"/> — is what the flakiness
    /// surfaces key off: a test whose only "retry" was a stall kill told us nothing about its
    /// reliability.
    /// </summary>
    public bool WasRetriedForFailure
        => Attempts.Count > 1 && Attempts.Take(Attempts.Count - 1).Any(a => !a.StallInduced);

    /// <summary>
    /// The honest three-way status. A test that needed retries reports
    /// <see cref="RunOutcome.PassOnRetry"/> and is never counted as a clean pass — collapsing
    /// the two is how a retry feature turns into a way to launder red into green. The one
    /// exception runs the other way: a pass whose only earlier attempt was stall-induced is a
    /// clean pass, because the test passed the only time it actually ran — the stall story is
    /// told by <see cref="SupervisorResults.StalledTests"/> and
    /// <see cref="SupervisorResults.StallKills"/>, not by the flaky ledger.
    /// </summary>
    public RunOutcome Outcome
    {
        get
        {
            if (Attempts.Any(a => a.Disposition.Kind == DispositionKind.AbortRun)) return RunOutcome.Aborted;
            if (!Final.Succeeded) return RunOutcome.Failed;
            return WasRetriedForFailure ? RunOutcome.PassOnRetry : RunOutcome.CleanPass;
        }
    }

    /// <summary>True when the run never established what this test does — a worker died.</summary>
    public bool IsIndeterminate => Final.Outcome.State == WorkerTestState.Indeterminate;

    public IEnumerable<string> UnsupportedDispositions
        => Attempts.Where(a => a.Unsupported is not null).Select(a => a.Unsupported!);
}

/// <summary>The whole run.</summary>
/// <summary>
/// A worker the supervisor killed to clear a stalled test (issue #173): who stalled, how far
/// past the threshold it was, and which process died for it.
/// </summary>
/// <param name="Lane">The lane whose worker was killed; null when the process ran one test alone.</param>
public sealed record StallKill(
    string Uid, string DisplayName, TimeSpan InFlight, int? Lane, int? ProcessId);

public sealed class SupervisorResults
{
    public required IReadOnlyList<TestReport> Tests { get; init; }

    /// <summary>Set when a policy aborted the run, or the environment made it impossible.</summary>
    public string? AbortReason { get; init; }

    /// <summary>
    /// True when this view came from <see cref="Supervisor.Snapshot"/> — "the run so far",
    /// not the run's verdict (issue #150). A ledger writer consuming a cancelled run's
    /// snapshot labels it with this rather than passing it off as a finished run; tests the
    /// run never got a verdict for appear as <see cref="WorkerTestState.Indeterminate"/>.
    /// </summary>
    public bool IsPartial { get; init; }

    /// <summary>Worker processes launched. Surfaced because isolation is not free.</summary>
    public int WorkersLaunched { get; init; }

    /// <summary>
    /// Wall clock for the whole run, preflight and discovery included. Stamped by the supervisor
    /// itself, so it is the one number that accounts for everything the run did rather than just
    /// the part inside a test.
    /// </summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary>
    /// Total time spent launching worker processes — the harness's own cost, made visible.
    /// </summary>
    /// <remarks>
    /// A sum across launches, not a wall-clock span: lanes launch concurrently, so on a parallel
    /// run this exceeds the wall clock the launches actually occupied. It is the right number for
    /// "what does a process cost us", which is what decides whether more isolation is affordable.
    /// </remarks>
    public TimeSpan WorkerLaunchTime { get; internal set; }

    /// <summary>
    /// Every worker death, with its exit code and last standard error. Without these,
    /// <see cref="Indeterminate"/> tells a user that something went wrong but nothing about what.
    /// </summary>
    public IReadOnlyList<string> WorkerFaults { get; init; } = [];

    /// <summary>
    /// Resources recycled during the run, in order. Reported because throwing a broker away and
    /// standing a new one up is expensive, and a suite that does it constantly is telling you
    /// something.
    /// </summary>
    public IReadOnlyList<string> Recyclings { get; init; } = [];

    /// <summary>
    /// Tests reported as stalled during the run (issue #145) — in flight past their threshold,
    /// in detection order, once per attempt. Always empty unless
    /// <c>Supervisor.StallThreshold</c> or <c>StallThresholdFor</c> was configured. A stalled
    /// test that eventually finished still appears here: it exceeded the budget its author or
    /// operator set, and a green run is exactly where that fact would otherwise go unnoticed.
    /// </summary>
    public IReadOnlyList<StalledTest> StalledTests { get; init; } = [];

    /// <summary>
    /// Workers the supervisor killed to clear a stalled test (issue #173), in kill order. Empty
    /// unless <see cref="Supervisor.StallAction"/> is <see cref="StallAction.KillAndRetry"/> and
    /// a stall actually fired. Survives into the report even on a green run — a run that only
    /// stayed green because a wedged worker was shot is a fact the summary must not hide.
    /// </summary>
    public IReadOnlyList<StallKill> StallKills { get; init; } = [];

    /// <summary>
    /// Each sampled worker's memory story (issue #149) — first, peak and last resident set.
    /// Empty unless <c>Supervisor.ResourceSampleInterval</c> was configured; a worker that
    /// could not be measured contributes nothing rather than zeroes.
    /// </summary>
    public IReadOnlyList<WorkerMemory> WorkerMemory { get; init; } = [];

    /// <summary>
    /// The RSS delta across each measured attempt (issue #149), null where overlapping tests
    /// made the delta unattributable. <see cref="RunResources.For"/> is the reporting view.
    /// </summary>
    public IReadOnlyList<TestMemory> TestMemory { get; init; } = [];

    public IReadOnlyList<TestReport> CleanPasses
        => Tests.Where(t => t.Outcome == RunOutcome.CleanPass).ToList();

    /// <summary>The run's flakiness ledger — reported separately from clean passes, always.</summary>
    public IReadOnlyList<TestReport> PassedOnRetry
        => Tests.Where(t => t.Outcome == RunOutcome.PassOnRetry).ToList();

    public IReadOnlyList<TestReport> Failed
        => Tests.Where(t => t.Outcome is RunOutcome.Failed or RunOutcome.Aborted).ToList();

    /// <summary>Tests whose result was never established. Never silently counted as passing.</summary>
    public IReadOnlyList<TestReport> Indeterminate
        => Tests.Where(t => t.IsIndeterminate).ToList();

    public int RetriesPerformed => Tests.Sum(t => t.AttemptCount - 1);

    /// <summary>
    /// Tests that needed more than one attempt, whether or not they eventually passed — the set
    /// worth quarantining.
    /// </summary>
    /// <remarks>
    /// Membership is "was retried", not "eventually failed", on purpose. A test that passes on
    /// the third attempt every run is unreliable, and a green build is exactly the situation in
    /// which that fact would otherwise go unnoticed. "Flaky under broker contention" is also
    /// precisely the behavioural insight the AI outbox wants. Retried <em>for its own
    /// failure</em>, though — a test whose worker the supervisor killed over a stall (its own
    /// or a batch-mate's, issue #173) said nothing about its reliability, so stall-induced
    /// attempts do not put it here; those live on <see cref="StallKills"/>.
    /// </remarks>
    public IReadOnlyList<TestReport> Quarantine => Tests.Where(t => t.WasRetriedForFailure).ToList();

    public IReadOnlyList<string> UnsupportedDispositions
        => Tests.SelectMany(t => t.UnsupportedDispositions).Distinct().ToList();

    /// <summary>0 = pass, 1 = failures, 2 = the run was aborted or a result is unknown.</summary>
    /// <remarks>
    /// Indeterminate maps to 2 rather than 1 on purpose: "some tests failed" and "we do not know
    /// what happened" are different operational situations, and the second one should not be
    /// mistaken for an ordinary red build.
    /// </remarks>
    public int ExitCode
    {
        get
        {
            if (AbortReason is not null || Tests.Any(t => t.Outcome == RunOutcome.Aborted)) return 2;
            if (Indeterminate.Count > 0) return 2;
            return Failed.Count > 0 ? 1 : 0;
        }
    }

    /// <summary>A short, honest summary line.</summary>
    public string Summarize()
    {
        var partial = IsPartial
            ? $"PARTIAL — the run was still in flight when this view was taken{Environment.NewLine}"
            : "";

        var parts = new List<string> { $"{CleanPasses.Count} passed" };

        if (PassedOnRetry.Count > 0) parts.Add($"{PassedOnRetry.Count} passed on retry");
        if (Failed.Count > 0) parts.Add($"{Failed.Count} failed");
        if (Indeterminate.Count > 0) parts.Add($"{Indeterminate.Count} indeterminate");
        if (StallKills.Count > 0) parts.Add($"{StallKills.Count} stall kill(s)");

        var summary = string.Join(", ", parts);
        summary = $"{partial}{summary} ({RetriesPerformed} retries, {WorkersLaunched} worker processes)";

        // An indeterminate count with no explanation is not a report. Lead with the reason.
        if (Recyclings.Count > 0)
        {
            summary += $"{Environment.NewLine}Recycled: {string.Join(", ", Recyclings)}";
        }

        if (WorkerFaults.Count > 0)
        {
            summary += $"{Environment.NewLine}Worker faults:{Environment.NewLine}  " +
                       string.Join($"{Environment.NewLine}  ", WorkerFaults);
        }

        return summary;
    }
}

/// <summary>
/// Carries a worker's failure into the policy layer. The wire has no exception type, so this is
/// reconstructed and best-effort — see <see cref="MtpWorkerClient"/>.
/// </summary>
public sealed class WorkerFailureException(string? type, string message) : Exception(message)
{
    /// <summary>The exception type name, when the worker's framework happened to include one.</summary>
    public string? ReportedType { get; } = type;
}
