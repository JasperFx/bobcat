using System.Text.Json;
using System.Text.Json.Serialization;
using Bobcat.Engine;

namespace Bobcat.Ledger;

/// <summary>
/// One test's observation from one run — the ledger's unit of record. Everything here is a
/// fact the run already established; nothing is derived, so folding observations in any order
/// always produces the same ledger.
/// </summary>
/// <param name="RunId">The run this was observed in — the dedup key alongside <see cref="Key"/>.</param>
/// <param name="At">When the run happened, supplied by the caller that owned the run's clock.</param>
/// <param name="Uid">
/// The platform's uid for the test in the run that observed it. Not the ledger's key: xUnit v3's
/// uid hashes the assembly path, so it changes across machines, configurations and TFMs. A ledger
/// read back from a file has no uid for a test keyed by display name, and carries the key here.
/// </param>
/// <param name="DisplayName">The test's display name — the ledger's key (issue #730), see <see cref="Key"/>.</param>
public sealed record LedgerRun(
    string RunId,
    DateTimeOffset At,
    string Uid,
    string DisplayName,
    string Outcome,
    int Attempts)
{
    private readonly string? _key;

    /// <summary>
    /// What the ledger files this observation under: the display name, which is stable across
    /// machines, unless two tests shared it in the run (then the uid, so two tests' durations are
    /// never merged) or it is blank. <see cref="LedgerKeys.For"/> decides it for a whole run;
    /// left unset it is the display name, or the uid when that is blank.
    /// </summary>
    public string Key
    {
        get => _key ?? (string.IsNullOrWhiteSpace(DisplayName) ? Uid : DisplayName);
        init => _key = value;
    }

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

/// <summary>
/// The ledger's key rule (issue #730), shared by the feeds that record observations and by the
/// scheduler that looks durations up, so both sides of the join name a test the same way.
/// </summary>
public static class LedgerKeys
{
    /// <summary>
    /// Each test's ledger key, by uid: its display name, which survives a change of machine,
    /// configuration or target framework where a uid does not — except where two of
    /// <paramref name="tests"/> share a display name, or it is blank, when the key is the uid.
    /// </summary>
    public static IReadOnlyDictionary<string, string> For(IEnumerable<(string Uid, string DisplayName)> tests)
    {
        var list = tests.ToList();
        var shared = list
            .Where(t => !string.IsNullOrWhiteSpace(t.DisplayName))
            .GroupBy(t => t.DisplayName, StringComparer.Ordinal)
            .Where(g => g.Select(t => t.Uid).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (uid, displayName) in list)
        {
            keys[uid] = string.IsNullOrWhiteSpace(displayName) || shared.Contains(displayName) ? uid : displayName;
        }

        return keys;
    }
}

/// <summary>A test whose recorded cost grew — invisible in any single run, obvious in a trend.</summary>
/// <param name="Key">The test's ledger key: its display name, or its uid where the name was shared.</param>
public sealed record DurationTrend(
    string Key, string DisplayName, TimeSpan Then, TimeSpan Now, double GrowthFactor);

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
/// The cross-run test ledger — the "one store, not two" issues #44 (layer 2) and
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
/// <b>Keyed by display name</b> (issue #730). xUnit v3's uid hashes the assembly path, so a
/// uid-keyed ledger matched 0 tests on another machine, configuration or TFM; the display name
/// matched Marten's 2521 of 2525. Where two tests share a display name in a run, both fall back
/// to their uids (<see cref="LedgerKeys"/>), and <see cref="SharedDisplayNames"/> says so.
/// </para>
/// <para>
/// <b>The file is compact</b> (issue #698): unindented, the key written once per test, and the
/// display name written only for a test keyed by its uid. Such a test's display name is its
/// newest observation's; the older ones take it too, in memory as on disk.
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
    /// <summary>
    /// The current on-disk schema version. 3 keys tests by display name (issue #730); 2 was the
    /// compact form keyed by uid (issue #698). Version 1 and 2 files still load — re-keyed by
    /// display name where it is present and no two of their tests shared it in one run — and
    /// are written back as version 3.
    /// </summary>
    public const int Version = 3;

    /// <summary>
    /// The default retention. Small, because the ledger is a CI artifact downloaded and
    /// uploaded every run (issue #730): five runs give a stable median and a usable trend.
    /// </summary>
    public const int DefaultMaxRunsPerTest = 5;

    private readonly SortedDictionary<string, List<LedgerRun>> _tests;

    private TestLedger(int maxRunsPerTest, SortedDictionary<string, List<LedgerRun>> tests)
    {
        MaxRunsPerTest = maxRunsPerTest;
        _tests = tests;
    }

    /// <summary>How many observations survive per test — the deterministic aging knob.</summary>
    public int MaxRunsPerTest { get; }

    /// <summary>Every test's retained observations, newest first, keyed and sorted by <see cref="LedgerRun.Key"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LedgerRun>> Tests
        => _tests.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<LedgerRun>)pair.Value);

    /// <summary>
    /// Display names two tests shared in one run, whose observations are therefore keyed by
    /// uid — the collision recorded rather than merged, and a reason a duration may not match
    /// on another machine.
    /// </summary>
    public IReadOnlyList<string> SharedDisplayNames
        => _tests
            .Where(pair => pair.Value.Count > 0)
            .Select(pair => (pair.Key, pair.Value[0].DisplayName))
            .Where(t => !string.IsNullOrWhiteSpace(t.DisplayName) && t.DisplayName != t.Key)
            .Select(t => t.DisplayName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    public static TestLedger Empty(int maxRunsPerTest = DefaultMaxRunsPerTest)
        => new(maxRunsPerTest, new SortedDictionary<string, List<LedgerRun>>(StringComparer.Ordinal));

    /// <summary>
    /// Fold observations in: union by (key, run id), then the deterministic prune. Two of the
    /// incoming observations from one run under one key with different uids are two tests that
    /// share a display name, and are filed by uid instead — never merged.
    /// </summary>
    public TestLedger Record(IEnumerable<LedgerRun> observations)
    {
        var tests = new SortedDictionary<string, List<LedgerRun>>(StringComparer.Ordinal);
        foreach (var pair in _tests) tests[pair.Key] = pair.Value.ToList();

        foreach (var observation in separateSharedKeys(observations))
        {
            if (!tests.TryGetValue(observation.Key, out var runs))
            {
                runs = [];
                tests[observation.Key] = runs;
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
    /// Freezes each observation's key, and files by uid any that share a key and a run with an
    /// observation of another uid: a feed that did not use <see cref="LedgerKeys"/> must still
    /// not have two tests' durations merged.
    /// </summary>
    private static List<LedgerRun> separateSharedKeys(IEnumerable<LedgerRun> observations)
    {
        var list = observations.ToList();
        var shared = list
            .GroupBy(r => (r.Key, r.RunId))
            .Where(g => g.Select(r => r.Uid).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        return list
            .Select(r => r with { Key = shared.Contains((r.Key, r.RunId)) ? r.Uid : r.Key })
            .ToList();
    }

    /// <summary>
    /// Union two ledgers — the whole conflict-resolution story. Idempotent, and commutative
    /// between ledgers of the same retention, so merging two sides of one committed file in
    /// either direction, or repeatedly, converges on the same file.
    /// </summary>
    /// <remarks>
    /// The result keeps <b>this</b> ledger's <see cref="MaxRunsPerTest"/>: the ledger merged
    /// into is the one being kept, and its retention is the one somebody chose for it. Keeping
    /// the larger of the two (as before issue #698) meant one artifact written at the default
    /// retention grew a committed file for good. To change a ledger's retention, merge it into
    /// <see cref="Empty"/> at the retention wanted.
    /// </remarks>
    public TestLedger Merge(TestLedger other)
    {
        var merged = new TestLedger(
            MaxRunsPerTest,
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
    /// folds of the same observations cannot disagree. Every observation takes the newest one's
    /// display name, because the file stores it once per test.
    /// </summary>
    private List<LedgerRun> prune(List<LedgerRun> runs)
    {
        var kept = runs
            .OrderByDescending(r => r.At)
            .ThenByDescending(r => r.RunId, StringComparer.Ordinal)
            .GroupBy(r => r.RunId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(r => r.At)
            .ThenByDescending(r => r.RunId, StringComparer.Ordinal)
            .Take(MaxRunsPerTest)
            .ToList();

        if (kept.Count == 0) return kept;

        var displayName = kept[0].DisplayName;
        return kept
            .Select(r => r.DisplayName == displayName ? r : r with { DisplayName = displayName })
            .ToList();
    }

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
    /// <param name="minRuns">
    /// Measured runs required before a trend is worth stating — within the default retention
    /// (<see cref="DefaultMaxRunsPerTest"/>), so a trend can be stated at all without raising it.
    /// </param>
    /// <param name="factor">How much growth earns a line. Below it, silence.</param>
    public IReadOnlyList<DurationTrend> Trends(int minRuns = 4, double factor = 2)
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
            var tests = group.Select(r => r.Key).Distinct(StringComparer.Ordinal)
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
        var because = $"Because = \"cleared by retry {cleared} time(s) in the ledger" +
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// The compact form: the ledger key names each test once, and the display name is written
    /// only where it differs from the key (a test keyed by uid). Version 2 had the same shape
    /// keyed by uid.
    /// </summary>
    private sealed class Document
    {
        public int Version { get; set; } = TestLedger.Version;
        public int MaxRunsPerTest { get; set; }
        public SortedDictionary<string, DocumentTest> Tests { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class DocumentTest
    {
        /// <summary>Left out when it is the key itself.</summary>
        public string? DisplayName { get; set; }

        public List<DocumentRun> Runs { get; set; } = [];
    }

    private sealed class DocumentRun
    {
        public string RunId { get; set; } = "";
        public DateTimeOffset At { get; set; }
        public string Outcome { get; set; } = "";
        public int Attempts { get; set; }
        public long? TotalMs { get; set; }
        public long? FirstMs { get; set; }
        public string? Failure { get; set; }
        public string? ClearedBy { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool StallInduced { get; set; }
    }

    /// <summary>Version 1: every observation carried its uid and display name. Read, never written.</summary>
    private sealed class DocumentVersion1
    {
        public int MaxRunsPerTest { get; set; }
        public Dictionary<string, List<LedgerRun>> Tests { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The canonical text: tests sorted by key, runs newest first, invariant formatting, no
    /// indentation — the same observations always serialize to the same bytes, whoever folded them.
    /// </summary>
    public string ToJson()
    {
        var document = new Document { MaxRunsPerTest = MaxRunsPerTest };
        foreach (var (key, runs) in _tests)
        {
            var displayName = runs.Count > 0 ? runs[0].DisplayName : null;
            document.Tests[key] = new DocumentTest
            {
                DisplayName = displayName == key ? null : displayName,
                Runs = runs.Select(r => new DocumentRun
                {
                    RunId = r.RunId,
                    At = r.At,
                    Outcome = r.Outcome,
                    Attempts = r.Attempts,
                    TotalMs = r.TotalMs,
                    FirstMs = r.FirstMs,
                    Failure = r.Failure,
                    ClearedBy = r.ClearedBy,
                    StallInduced = r.StallInduced
                }).ToList()
            };
        }

        return JsonSerializer.Serialize(document, json);
    }

    public static TestLedger FromJson(string text)
    {
        var version = JsonSerializer.Deserialize<VersionProbe>(text, json)?.Version
                      ?? throw new BobcatConfigurationException("the ledger file was empty");

        int maxRunsPerTest;
        IEnumerable<LedgerRun> observations;

        if (version <= 1)
        {
            var legacy = JsonSerializer.Deserialize<DocumentVersion1>(text, json)!;
            maxRunsPerTest = legacy.MaxRunsPerTest;
            observations = rekeyUidKeyed(legacy.Tests.Values.SelectMany(runs => runs));
        }
        else if (version <= Version)
        {
            var document = JsonSerializer.Deserialize<Document>(text, json)!;
            maxRunsPerTest = document.MaxRunsPerTest;
            var read = document.Tests.SelectMany(pair => pair.Value.Runs.Select(r =>
                new LedgerRun(r.RunId, r.At, pair.Key, pair.Value.DisplayName ?? pair.Key, r.Outcome, r.Attempts)
                {
                    Key = pair.Key,
                    TotalMs = r.TotalMs,
                    FirstMs = r.FirstMs,
                    Failure = r.Failure,
                    ClearedBy = r.ClearedBy,
                    StallInduced = r.StallInduced
                }));

            // Version 2 was keyed by uid, so its key is a uid to re-key from.
            observations = version == Version ? read : rekeyUidKeyed(read);
        }
        else
        {
            throw new BobcatConfigurationException(
                $"the ledger file is version {version}, newer than this Bobcat reads (version {Version}); upgrade Bobcat to fold it");
        }

        return Empty(maxRunsPerTest > 0 ? maxRunsPerTest : DefaultMaxRunsPerTest).Record(observations);
    }

    /// <summary>
    /// A version 1 or 2 file was keyed by uid. Its observations are re-keyed by display name,
    /// which folds one test's uids from different machines together — except a display name
    /// two uids carried in one run, which was two tests, and stays keyed by uid.
    /// </summary>
    private static IEnumerable<LedgerRun> rekeyUidKeyed(IEnumerable<LedgerRun> observations)
    {
        var list = observations.ToList();
        var shared = list
            .Where(r => !string.IsNullOrWhiteSpace(r.DisplayName))
            .GroupBy(r => (r.DisplayName, r.RunId))
            .Where(g => g.Select(r => r.Uid).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key.DisplayName)
            .ToHashSet(StringComparer.Ordinal);

        return list.Select(r => r with
        {
            Key = string.IsNullOrWhiteSpace(r.DisplayName) || shared.Contains(r.DisplayName) ? r.Uid : r.DisplayName
        });
    }

    private sealed class VersionProbe
    {
        public int Version { get; set; } = 1;
    }

    /// <summary>
    /// Load a ledger — typically the previous CI run's artifact, or a committed file; a missing
    /// file is an empty ledger, not an error.
    /// </summary>
    /// <param name="path">The ledger file.</param>
    /// <param name="maxRunsPerTest">
    /// The retention to load at. Left out, an existing file keeps the retention written in it
    /// and a missing one gets <see cref="DefaultMaxRunsPerTest"/>. Given, it wins over the
    /// file's in either direction (issue #746): a smaller one prunes each test to its newest
    /// runs, a larger one keeps more from the next <see cref="Record"/> on, and
    /// <see cref="Save"/> writes it back.
    /// </param>
    public static TestLedger Load(string path, int? maxRunsPerTest = null)
    {
        if (!File.Exists(path)) return Empty(maxRunsPerTest ?? DefaultMaxRunsPerTest);

        var loaded = FromJson(File.ReadAllText(path));
        return maxRunsPerTest is { } wanted && wanted != loaded.MaxRunsPerTest
            ? Empty(wanted).Merge(loaded)
            : loaded;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, ToJson() + Environment.NewLine);
    }
}
