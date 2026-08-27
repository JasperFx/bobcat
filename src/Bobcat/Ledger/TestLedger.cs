using System.Text.Json;
using System.Text.Json.Serialization;
using Bobcat.Engine;

namespace Bobcat.Ledger;

/// <summary>
/// One test's observation from one run — the ledger's unit of record. Everything here is a
/// fact the run already established; nothing is derived, so folding observations in any order
/// always produces the same ledger.
/// </summary>
/// <param name="RunId">The run this was observed in — the dedup key alongside <paramref name="Uid"/>.</param>
/// <param name="At">When the run happened, supplied by the caller that owned the run's clock.</param>
/// <param name="Uid">The spec identity <c>{Feature}/{Scenario}</c> — the same key everything else uses.</param>
public sealed record LedgerRun(
    string RunId,
    DateTimeOffset At,
    string Uid,
    string DisplayName,
    string Outcome,
    int Attempts)
{
    /// <summary>
    /// What the test cost the run across every attempt, in milliseconds. Null when the
    /// framework reported no duration (tUnit erases them on the MTP wire) — unmeasured is
    /// never zero, the same rule as <c>RunTiming.Unmeasured</c>.
    /// </summary>
    public long? TotalMs { get; init; }

    /// <summary>The first attempt alone — what the test would cost if it never flaked. Null when unmeasured.</summary>
    public long? FirstMs { get; init; }

    /// <summary>
    /// The exception type name of the first failing attempt, when one was reported. Null when
    /// the test passed cleanly, or when the framework erased the type — a name is all there
    /// ever is out of process, which is exactly why <c>FailureSignature</c> matches on names.
    /// </summary>
    public string? Failure { get; init; }

    /// <summary>
    /// The <c>DispositionKind</c> name of the retry that preceded the recovery, when the test
    /// passed on retry — the evidence a recovery-hint proposal is made from. Null otherwise.
    /// </summary>
    public string? ClearedBy { get; init; }

    /// <summary>
    /// True when the supervisor's stall escalation manufactured any of this run's attempts
    /// (issue #173). A wedge is not a flake: these entries never feed the hint proposals.
    /// </summary>
    public bool StallInduced { get; init; }
}

/// <summary>A test whose recorded cost grew — invisible in any single run, obvious in a trend.</summary>
public sealed record DurationTrend(
    string Uid, string DisplayName, TimeSpan Then, TimeSpan Now, double GrowthFactor);

/// <summary>
/// A recovery hint the ledger's evidence supports — <b>a proposal, never a policy</b>. The
/// decided fork from issue #44 stands: the ledger may propose a hint, but a human accepts it by
/// writing the attribute into the code, because a policy that silently learns "just retry this"
/// is exactly how red gets laundered into green with nobody deciding to. Nothing in Bobcat
/// reads these back into a <c>IFailurePolicy</c>, and nothing ever should.
/// </summary>
public sealed record HintProposal(
    string FailureTypeName,
    string ClearedBy,
    int Cleared,
    int Unrecovered,
    string Suggestion,
    IReadOnlyList<string> Tests);

/// <summary>
/// The committed, cross-run test ledger — the "one store, not two" issues #44 (layer 2) and
/// #56/#142 (layers 2–3) both deferred to, and the third consumer's data source:
/// <c>Supervisor.KnownTestDurations</c> feeds <c>WorkPlan</c>'s longest-processing-time-first
/// balancer, which without history falls back to test count on every first run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The merge strategy is the design</b> (issue #142: "merge strategy is what decides whether
/// people keep the file or delete it in annoyance"). The ledger is a grow-only set of
/// per-(test, run) observations plus a deterministic prune — a CRDT in the only sense that
/// matters here: <see cref="Record"/> and <see cref="Merge"/> are commutative, associative and
/// idempotent, and serialization is canonical (sorted tests, newest-first runs, invariant
/// formatting), so the same observations produce byte-identical files whoever folds them in
/// whatever order. A git conflict between two independently-updated ledgers is therefore
/// always resolvable by loading both sides, merging, and saving — no run artifacts needed,
/// no judgement calls, and a custom merge driver could automate exactly that later.
/// </para>
/// <para>
/// <b>Derived, never primary.</b> The primary record of a run is its report artifact (the
/// supervisor's JSON, the runner's suite JSON, the monitor's archives); the ledger is a
/// compaction of those, and advisory everywhere it is consumed — a stale or absent ledger
/// degrades lane balancing and trend reporting, never correctness. That is what makes any CI
/// collection topology safe: a nightly single-writer job, a per-PR local fold, or no automation
/// at all.
/// </para>
/// <para>
/// <b>Aging is run-count-based and clock-free.</b> <see cref="MaxRunsPerTest"/> newest
/// observations survive per test, ordered by their own stamps — the fold never asks what time
/// it is, which is half of what makes it deterministic. A test that stops existing keeps its
/// entries until <see cref="PruneTestsNotSeenSince"/> is called with an explicit cutoff; the
/// caller owns that clock too.
/// </para>
/// </remarks>
public sealed class TestLedger
{
    /// <summary>The current on-disk schema version.</summary>
    public const int Version = 1;

    private readonly SortedDictionary<string, List<LedgerRun>> _tests;

    private TestLedger(int maxRunsPerTest, SortedDictionary<string, List<LedgerRun>> tests)
    {
        MaxRunsPerTest = maxRunsPerTest;
        _tests = tests;
    }

    /// <summary>How many observations survive per test — the deterministic aging knob.</summary>
    public int MaxRunsPerTest { get; }

    /// <summary>Every test's retained observations, newest first, keyed and sorted by uid.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LedgerRun>> Tests
        => _tests.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<LedgerRun>)pair.Value);

    public static TestLedger Empty(int maxRunsPerTest = 20)
        => new(maxRunsPerTest, new SortedDictionary<string, List<LedgerRun>>(StringComparer.Ordinal));

    /// <summary>Fold observations in: union by (uid, run id), then the deterministic prune.</summary>
    public TestLedger Record(IEnumerable<LedgerRun> observations)
    {
        var tests = new SortedDictionary<string, List<LedgerRun>>(StringComparer.Ordinal);
        foreach (var pair in _tests) tests[pair.Key] = pair.Value.ToList();

        foreach (var observation in observations)
        {
            if (!tests.TryGetValue(observation.Uid, out var runs))
            {
                runs = [];
                tests[observation.Uid] = runs;
            }

            runs.Add(observation);
        }

        foreach (var uid in tests.Keys.ToList())
        {
            tests[uid] = prune(tests[uid]);
        }

        return new TestLedger(MaxRunsPerTest, tests);
    }

    /// <summary>
    /// Union two ledgers — the whole conflict-resolution story. Commutative and idempotent, so
    /// merging in either direction, or repeatedly, converges on the same file.
    /// </summary>
    public TestLedger Merge(TestLedger other)
    {
        // The larger retention wins: shrinking a ledger because the other side was configured
        // smaller would silently discard history someone chose to keep.
        var merged = new TestLedger(
            Math.Max(MaxRunsPerTest, other.MaxRunsPerTest),
            new SortedDictionary<string, List<LedgerRun>>(StringComparer.Ordinal));

        return merged
            .Record(_tests.Values.SelectMany(runs => runs))
            .Record(other._tests.Values.SelectMany(runs => runs));
    }

    /// <summary>
    /// Drop tests whose newest observation predates <paramref name="cutoff"/> — the explicit
    /// half of aging, for tests that were renamed or deleted. The caller supplies the clock;
    /// the fold itself never reads one.
    /// </summary>
    public TestLedger PruneTestsNotSeenSince(DateTimeOffset cutoff)
    {
        var kept = new SortedDictionary<string, List<LedgerRun>>(StringComparer.Ordinal);
        foreach (var pair in _tests)
        {
            if (pair.Value.Count > 0 && pair.Value[0].At >= cutoff) kept[pair.Key] = pair.Value.ToList();
        }

        return new TestLedger(MaxRunsPerTest, kept);
    }

    /// <summary>
    /// Newest first, deduplicated by run id, capped at <see cref="MaxRunsPerTest"/> — the one
    /// ordering everything else derives from. Fully deterministic: stamp, then run id, so two
    /// folds of the same observations cannot disagree.
    /// </summary>
    private List<LedgerRun> prune(List<LedgerRun> runs)
        => runs
            .OrderByDescending(r => r.At)
            .ThenByDescending(r => r.RunId, StringComparer.Ordinal)
            .GroupBy(r => r.RunId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(r => r.At)
            .ThenByDescending(r => r.RunId, StringComparer.Ordinal)
            .Take(MaxRunsPerTest)
            .ToList();

    // ---- the three consumers ------------------------------------------------------------------

    /// <summary>
    /// The scheduler's feed: each test's median measured cost, ready to assign to
    /// <c>Supervisor.KnownTestDurations</c> so <c>WorkPlan</c> balances by duration on every
    /// run instead of only after a warm-up. Tests with no measured runs are absent, not zero —
    /// the balancer already charges absentees the median of what is known.
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan> KnownDurations()
    {
        var durations = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var pair in _tests)
        {
            var measured = pair.Value.Where(r => r.TotalMs is not null).Select(r => r.TotalMs!.Value).ToList();
            if (measured.Count == 0) continue;

            durations[pair.Key] = TimeSpan.FromMilliseconds(median(measured));
        }

        return durations;
    }

    /// <summary>
    /// Tests whose cost grew across the retained runs — #142's layer 3, the fact no single run
    /// can show. Report, don't act: whether a slow test is a bug or a genuinely slow
    /// integration test is a judgement, and this is the evidence for making it.
    /// </summary>
    /// <param name="minRuns">Measured runs required before a trend is worth stating.</param>
    /// <param name="factor">How much growth earns a line. Below it, silence.</param>
    public IReadOnlyList<DurationTrend> Trends(int minRuns = 6, double factor = 2)
    {
        var trends = new List<DurationTrend>();
        foreach (var pair in _tests)
        {
            // Oldest-first for the split: "then" is the older half, "now" the newer.
            var measured = pair.Value
                .Where(r => r.TotalMs is not null)
                .OrderBy(r => r.At).ThenBy(r => r.RunId, StringComparer.Ordinal)
                .Select(r => r.TotalMs!.Value)
                .ToList();
            if (measured.Count < minRuns) continue;

            var then = median(measured.Take(measured.Count / 2).ToList());
            var now = median(measured.Skip(measured.Count / 2).ToList());
            if (then <= 0) continue;

            var growth = (double)now / then;
            if (growth < factor) continue;

            trends.Add(new DurationTrend(
                pair.Key,
                pair.Value[0].DisplayName,
                TimeSpan.FromMilliseconds(then),
                TimeSpan.FromMilliseconds(now),
                growth));
        }

        return trends.OrderByDescending(t => t.GrowthFactor).ToList();
    }

    /// <summary>
    /// Recovery hints the retained evidence supports — #44's layer 2, on the decided terms:
    /// <b>the ledger proposes, a human accepts</b> by writing the attribute into the code.
    /// These are never fed to a policy; see <see cref="HintProposal"/> for why that fork stays
    /// closed.
    /// </summary>
    /// <param name="minOccurrences">
    /// How many recoveries a failure class needs before proposing anything — one lucky retry
    /// is an anecdote, not evidence.
    /// </param>
    public IReadOnlyList<HintProposal> ProposeHints(int minOccurrences = 3)
    {
        // A wedge is not a flake (issue #173): stall-induced entries carry no information
        // about the failure class and are excluded before anything is counted.
        var withFailures = _tests.Values
            .SelectMany(runs => runs)
            .Where(r => r is { Failure: not null, StallInduced: false })
            .ToList();

        var proposals = new List<HintProposal>();
        foreach (var group in withFailures.GroupBy(r => r.Failure!, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var cleared = group.Where(r => r.ClearedBy is not null).ToList();
            var unrecovered = group.Count(r => r.Outcome == nameof(Resilience.RunOutcome.Failed));
            var tests = group.Select(r => r.Uid).Distinct(StringComparer.Ordinal)
                .OrderBy(u => u, StringComparer.Ordinal).ToList();

            if (cleared.Count >= minOccurrences)
            {
                // The dominant recovery is the proposal; ties break deterministically by name.
                var by = cleared.GroupBy(r => r.ClearedBy!, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .First().Key;

                proposals.Add(new HintProposal(group.Key, by, cleared.Count, unrecovered,
                    suggest(group.Key, by, cleared.Count, unrecovered), tests));
            }
            else if (cleared.Count == 0 && unrecovered >= minOccurrences &&
                     group.Any(r => r.Attempts > 1))
            {
                // The counterweight: retried and never once recovered — the evidence
                // NeverRecovers exists to state.
                proposals.Add(new HintProposal(group.Key, "never", 0, unrecovered,
                    $"[NeverRecovers(typeof({shortName(group.Key)}), " +
                    $"Because = \"retried across {unrecovered} run(s) and never recovered\")]",
                    tests));
            }
        }

        return proposals;
    }

    private static string suggest(string failure, string clearedBy, int cleared, int unrecovered)
    {
        var because = $"Because = \"cleared by retry {cleared} time(s) in the committed ledger" +
                      (unrecovered > 0 ? $"; {unrecovered} run(s) never recovered" : "") + "\"";

        return clearedBy switch
        {
            "RetryInFreshProcess" => $"[ClearsInFreshProcess(typeof({shortName(failure)}), {because})]",
            // The ledger records the disposition kind, not which resources were recycled —
            // the human names those, which is rather the point of the fork.
            "RetryAfterRecycle" => $"[ClearsOnRecycle(\"<name the resources>\", typeof({shortName(failure)}), {because})]",
            _ => $"[ClearsOnRetry(typeof({shortName(failure)}), {because})]"
        };
    }

    private static string shortName(string typeName)
    {
        var dot = typeName.LastIndexOf('.');
        return dot < 0 ? typeName : typeName[(dot + 1)..];
    }

    private static long median(List<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    // ---- canonical serialization --------------------------------------------------------------

    private static readonly JsonSerializerOptions json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class Document
    {
        public int Version { get; set; } = TestLedger.Version;
        public int MaxRunsPerTest { get; set; }
        public SortedDictionary<string, List<LedgerRun>> Tests { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The canonical text: tests sorted by uid, runs newest first, invariant formatting — the
    /// same observations always serialize to the same bytes, whoever folded them.
    /// </summary>
    public string ToJson()
        => JsonSerializer.Serialize(new Document { MaxRunsPerTest = MaxRunsPerTest, Tests = _tests }, json);

    public static TestLedger FromJson(string text)
    {
        var document = JsonSerializer.Deserialize<Document>(text, json)
                       ?? throw new BobcatConfigurationException("the ledger file was empty");

        var ledger = Empty(document.MaxRunsPerTest > 0 ? document.MaxRunsPerTest : 20);
        return ledger.Record(document.Tests.Values.SelectMany(runs => runs));
    }

    /// <summary>Load a committed ledger; a missing file is an empty ledger, not an error.</summary>
    public static TestLedger Load(string path, int maxRunsPerTest = 20)
        => File.Exists(path) ? FromJson(File.ReadAllText(path)) : Empty(maxRunsPerTest);

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, ToJson() + Environment.NewLine);
    }
}
