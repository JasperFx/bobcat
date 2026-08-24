namespace Bobcat.Supervisor;

/// <summary>
/// Where the run spent its memory — RunTiming's sibling, computed over
/// <see cref="SupervisorResults"/> (issue #149). The case that prompted it: a green CI job
/// whose test host grew 375 MB → 9334 MB across 275 tests and finished with 172 MB free on a
/// 16 GB runner — one added test class from the runner being OOM-killed with the logs
/// discarded. An external sampler produced that table but could not say which tests grew it;
/// the supervisor can, because it knows when each attempt starts and ends.
/// </summary>
/// <remarks>
/// Report, don't act — the same guardrail as <see cref="RunTiming"/>: a memory threshold
/// turned into a build failure converts a useful signal into a flaky one. Whether 9 GB is a
/// leak or a genuinely heavy integration suite is a judgement; this is the evidence for it.
/// Unmeasured is never zero-filled: a run with sampling off, or a client that cannot measure,
/// reports nothing rather than zeroes.
/// </remarks>
public sealed class RunResources
{
    private const double megabyte = 1024 * 1024;

    /// <summary>Every worker that produced at least one sample.</summary>
    public IReadOnlyList<WorkerMemory> Workers { get; private init; } = [];

    /// <summary>Attributed attempts, most memory retained first.</summary>
    public IReadOnlyList<TestMemory> Attributed { get; private init; } = [];

    /// <summary>
    /// Attempts that were measured but ran alongside others in their process, so their delta
    /// has no single owner. Declared rather than dropped — silently thinner attribution reads
    /// as "the other tests were free".
    /// </summary>
    public int Unattributed { get; private init; }

    /// <summary>False when nothing was sampled — sampling off, or nothing measurable.</summary>
    public bool IsMeasured => Workers.Count > 0;

    /// <summary>The highest resident set any worker ever showed. Null when unmeasured.</summary>
    public long? PeakBytes => Workers.Count == 0 ? null : Workers.Max(w => w.PeakBytes);

    /// <summary>The killer report: the attempts that grew their process most.</summary>
    public IReadOnlyList<TestMemory> TopRetainers(int count) => Attributed.Take(count).ToList();

    public static RunResources For(SupervisorResults results) => new()
    {
        Workers = results.WorkerMemory,
        Attributed = results.TestMemory
            .Where(test => test.RetainedBytes is not null)
            .OrderByDescending(test => test.RetainedBytes)
            .ToList(),
        Unattributed = results.TestMemory.Count(test => test.RetainedBytes is null)
    };

    /// <summary>Whole megabytes — the resolution the RSS story is told at.</summary>
    public static string Humanize(long bytes) => $"{Math.Round(bytes / megabyte):0} MB";

    /// <summary>A signed delta: "+8959 MB", "-12 MB".</summary>
    public static string Delta(long bytes) => bytes < 0 ? $"-{Humanize(-bytes)}" : $"+{Humanize(bytes)}";
}
