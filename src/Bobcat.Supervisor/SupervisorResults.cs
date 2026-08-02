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
    /// The honest three-way status. A test that needed retries reports
    /// <see cref="RunOutcome.PassOnRetry"/> and is never counted as a clean pass — collapsing
    /// the two is how a retry feature turns into a way to launder red into green.
    /// </summary>
    public RunOutcome Outcome
    {
        get
        {
            if (Attempts.Any(a => a.Disposition.Kind == DispositionKind.AbortRun)) return RunOutcome.Aborted;
            if (!Final.Succeeded) return RunOutcome.Failed;
            return WasRetried ? RunOutcome.PassOnRetry : RunOutcome.CleanPass;
        }
    }

    /// <summary>True when the run never established what this test does — a worker died.</summary>
    public bool IsIndeterminate => Final.Outcome.State == WorkerTestState.Indeterminate;

    public IEnumerable<string> UnsupportedDispositions
        => Attempts.Where(a => a.Unsupported is not null).Select(a => a.Unsupported!);
}

/// <summary>The whole run.</summary>
public sealed class SupervisorResults
{
    public required IReadOnlyList<TestReport> Tests { get; init; }

    /// <summary>Set when a policy aborted the run, or the environment made it impossible.</summary>
    public string? AbortReason { get; init; }

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
    /// precisely the behavioural insight the AI outbox wants.
    /// </remarks>
    public IReadOnlyList<TestReport> Quarantine => Tests.Where(t => t.WasRetried).ToList();

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
        var parts = new List<string> { $"{CleanPasses.Count} passed" };

        if (PassedOnRetry.Count > 0) parts.Add($"{PassedOnRetry.Count} passed on retry");
        if (Failed.Count > 0) parts.Add($"{Failed.Count} failed");
        if (Indeterminate.Count > 0) parts.Add($"{Indeterminate.Count} indeterminate");

        var summary = string.Join(", ", parts);
        summary = $"{summary} ({RetriesPerformed} retries, {WorkersLaunched} worker processes)";

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
