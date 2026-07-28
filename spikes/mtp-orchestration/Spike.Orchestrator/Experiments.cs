using System.Diagnostics;

namespace Spike.Orchestrator;

/// <summary>
/// The battery of experiments issue #43 asks for, one method per question. Each one both prints
/// what it saw and records a verdict, so the findings note is written from evidence rather than
/// from what the docs claim.
/// </summary>
public sealed class Experiments
{
    private const string FlakyTest = "flaky_until_told_otherwise";
    private const string TraitTest = "carries_traits";
    private const string CrashTest = "kills_the_process_when_armed";
    private const string SlowTest = "sleeps_when_armed";

    private readonly string _executable;
    private readonly string _name;
    private readonly bool _verbose;
    private readonly Report _report;

    public Experiments(string executable, string name, bool verbose, Report report)
    {
        _executable = executable;
        _name = name;
        _verbose = verbose;
        _report = report;
    }

    private TestHostSession NewSession(Dictionary<string, string>? environment = null)
        => new(_executable, environment) { Verbose = _verbose };

    // ---------------------------------------------------------------- Q1

    public async Task DiscoverAndRunEverything()
    {
        Console.WriteLine("\n-- Q1: enumerate + run + structured results");

        await using var session = NewSession();
        await session.Connect();

        var discovered = await session.Discover();
        var ran = await session.Run();

        foreach (var outcome in ran) Console.WriteLine("     " + outcome);

        var states = ran.Select(o => o.State).Distinct().OrderBy(s => s).ToList();
        var timed = ran.Count(o => o.Duration is not null);
        var withErrors = ran.Count(o => o.ErrorMessage is not null);

        var complete = discovered.Count > 0 && ran.Count == discovered.Count &&
                       states.Contains("passed") && states.Contains("failed") && states.Contains("skipped");

        _report.Add(_name, "Q1 — parent can enumerate + run + collect structured results", "discover+run",
            complete ? Verdict.Yes : Verdict.No,
            $"discovered {discovered.Count}, ran {ran.Count}; states [{string.Join(", ", states)}]; " +
            $"{timed} timed, {withErrors} carried error detail");

        // Q5's first half: does the wire separate an assertion failure from an escaped exception?
        var assertion = ran.FirstOrDefault(o => o.IsAssertionFailure);
        var error = ran.FirstOrDefault(o => o.IsError);

        _report.Add(_name, "Q5 — failure fidelity: assertion vs exception", "state separation",
            assertion is not null && error is not null ? Verdict.Yes : Verdict.No,
            assertion is not null && error is not null
                ? $"'failed' ({Short(assertion.DisplayName)}) is distinct from 'error' ({Short(error.DisplayName)})"
                : "host did not produce both states");

        var typed = ran.Where(o => o.IsError && o.ErrorType is not null).ToList();
        _report.Add(_name, "Q5 — failure fidelity: exception TYPE on the wire", "error.type",
            typed.Count > 0 ? Verdict.Partial : Verdict.No,
            typed.Count > 0
                ? $"no error.type field; type only recoverable by parsing the message ('{typed[0].ErrorType}')"
                : "exception type absent from the wire entirely — message carries no type prefix");

        var stacks = ran.Count(o => o.StackTrace is not null);
        _report.Add(_name, "Q5 — failure fidelity: stack traces", "error.stacktrace",
            stacks > 0 ? Verdict.Yes : Verdict.No, $"{stacks} outcomes carried a stack trace");
    }

    // ---------------------------------------------------------------- Q3 (identity)

    public async Task UidsAreStableAcrossProcesses()
    {
        Console.WriteLine("\n-- Q3: is test identity stable across separate processes?");

        var first = await DiscoverUids();
        var second = await DiscoverUids();

        var stable = first.Count > 0 && first.SetEquals(second);

        _report.Add(_name, "Q3 — test identity is stable across runs", "uid stability",
            stable ? Verdict.Yes : Verdict.No,
            stable
                ? $"{first.Count} uids identical across two processes (e.g. {Clip(first.First(), 44)})"
                : $"uids drifted: {first.Count} vs {second.Count}, {first.Intersect(second).Count()} shared");
    }

    private async Task<HashSet<string>> DiscoverUids()
    {
        await using var session = NewSession();
        await session.Connect();
        var discovered = await session.Discover();
        return discovered.Select(o => o.Uid).ToHashSet();
    }

    // ---------------------------------------------------------------- Q3 (selective re-run)

    public async Task SelectiveRerunOfOneTest()
    {
        Console.WriteLine("\n-- Q3: re-run ONE test by id (the RetryInProcess lever)");

        await using var session = NewSession();
        await session.Connect();

        var all = await session.Run();
        var flaky = all.FirstOrDefault(o => o.DisplayName.Contains(FlakyTest));

        if (flaky is null)
        {
            _report.Add(_name, "Q3 — selective re-run by test id", "run one uid", Verdict.No,
                "could not find the flaky test");
            return;
        }

        Console.WriteLine($"     first attempt: {flaky.State} — retrying just this uid in-process");

        // Same process, same connection, second attempt at exactly one test.
        var retried = await session.Run([flaky.Uid]);

        foreach (var line in session.HostStdout.Where(l => l.Contains("[bobcat-host]")))
            Console.WriteLine("     " + line);

        var onlyOne = retried.Count == 1;
        var isTheRightOne = retried.Count > 0 && retried[0].Uid == flaky.Uid;

        _report.Add(_name, "Q3 — selective re-run by test id", "run one uid",
            onlyOne && isTheRightOne ? Verdict.Yes : Verdict.No,
            onlyOne && isTheRightOne
                ? $"second attempt executed exactly 1 test ({Short(retried[0].DisplayName)}), state '{retried[0].State}'"
                : $"asked for 1 uid, host reported {retried.Count}");
    }

    // ---------------------------------------------------------------- Q3 (isolation)

    public async Task RunOneTestAloneInAFreshProcess()
    {
        Console.WriteLine("\n-- Q3: run one test ALONE in a fresh process (the [Isolated] lever)");

        string uid;
        await using (var probe = NewSession())
        {
            await probe.Connect();
            var discovered = await probe.Discover();
            var target = discovered.FirstOrDefault(o => o.DisplayName.Contains(FlakyTest));
            if (target is null)
            {
                _report.Add(_name, "Q3 — run a single test alone in a fresh process", "isolated run",
                    Verdict.No, "could not find the target test");
                return;
            }
            uid = target.Uid;
        }

        // A brand-new process, told up front to run exactly one test — and armed so the
        // normally-failing test passes, proving the environment belongs to this attempt alone.
        await using var isolated = NewSession(new Dictionary<string, string> { ["SPIKE_FLAKY_PASSES"] = "true" });
        await isolated.Connect();
        var outcomes = await isolated.Run([uid]);

        var alone = outcomes.Count == 1;
        var passed = alone && outcomes[0].State == "passed";

        _report.Add(_name, "Q3 — run a single test alone in a fresh process", "isolated run",
            alone && passed ? Verdict.Yes : alone ? Verdict.Partial : Verdict.No,
            alone
                ? $"fresh process ran exactly 1 test and it {outcomes[0].State} (per-process env honoured)"
                : $"fresh process ran {outcomes.Count} tests when asked for 1");
    }

    // ---------------------------------------------------------------- Q4

    public async Task TraitsSurviveTheWire()
    {
        Console.WriteLine("\n-- Q4: do traits/attributes reach the parent as metadata?");

        await using var session = NewSession();
        await session.Connect();
        var discovered = await session.Discover();

        var tagged = discovered.FirstOrDefault(o => o.DisplayName.Contains(TraitTest));
        var traits = tagged?.Traits ?? new Dictionary<string, string>();

        var hasBoth = traits.ContainsKey("Isolated") && traits.ContainsKey("RecycleOnRetry");

        _report.Add(_name, "Q4 — traits/attributes readable through the protocol", "traits at discovery",
            hasBoth ? Verdict.Yes : Verdict.No,
            hasBoth
                ? $"at DISCOVERY time: {string.Join(", ", traits.Select(t => $"{t.Key}={t.Value}"))}"
                : $"expected Isolated + RecycleOnRetry, got [{string.Join(", ", traits.Keys)}]");
    }

    // ---------------------------------------------------------------- Q5 (crash)

    public async Task HostCrashMidRun()
    {
        Console.WriteLine("\n-- Q5: what does the parent see when the host dies mid-run?");

        await using var session = NewSession(new Dictionary<string, string> { ["SPIKE_CRASH"] = "true" });
        await session.Connect();

        var (outcomes, ending) = await session.RunExpectingTrouble();
        var exitCode = await session.WaitForExit(TimeSpan.FromSeconds(10));

        var reported = outcomes.Count;
        var decided = outcomes.Count(o => o.State is "passed" or "failed" or "error" or "skipped");

        Console.WriteLine($"     {ending}");
        Console.WriteLine($"     host exit code: {exitCode?.ToString() ?? "(still running)"}");
        Console.WriteLine($"     partial results salvaged: {decided} decided of {reported} reported");

        var detectable = session.Fault is not null || exitCode is not null and not 0;

        _report.Add(_name, "Q5 — host crash is DETECTABLE by the parent", "process death",
            detectable ? Verdict.Yes : Verdict.No,
            detectable
                ? $"socket closed{(exitCode is not null ? $" + exit code {exitCode}" : "")}; " +
                  $"pending run request faulted rather than hanging"
                : "parent could not tell the host had died");

        // Separate question, and the answer is worse: MTP batches node updates, so how much of
        // the completed work reaches the parent before the process dies is not guaranteed.
        _report.Add(_name, "Q5 — partial results survive a crash", "salvage",
            decided > 0 ? Verdict.Partial : Verdict.No,
            decided > 0
                ? $"{decided} of the slice's outcomes arrived before death — salvage happens but is not guaranteed"
                : "nothing salvaged: every outcome in the in-flight slice was lost");
    }

    // ---------------------------------------------------------------- Q6 (cancellation)

    public async Task CancellationOfARunningTest()
    {
        Console.WriteLine("\n-- Q6: does in-band cancellation stop a running test?");

        await using var session = NewSession(new Dictionary<string, string> { ["SPIKE_SLOW"] = "true" });
        await session.Connect();

        var (outcomes, ending, elapsed) = await session.RunThenCancel(TimeSpan.FromSeconds(2));

        var slow = outcomes.FirstOrDefault(o => o.DisplayName.Contains(SlowTest));
        var stoppedEarly = elapsed < TimeSpan.FromSeconds(25);

        Console.WriteLine($"     {ending} after {elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"     slow test final state: {slow?.State ?? "(never reported)"}");

        _report.Add(_name, "Q6 — cancellation stops an in-flight run", "$/cancelRequest",
            stoppedEarly ? Verdict.Yes : Verdict.No,
            stoppedEarly
                ? $"run ended {elapsed.TotalSeconds:F1}s in (test sleeps 30s); {ending}; slow test='{slow?.State ?? "unreported"}'"
                : $"cancel had no effect — ran the full {elapsed.TotalSeconds:F0}s");
    }

    // ---------------------------------------------------------------- Q6 (startup)

    public async Task StartupCost()
    {
        Console.WriteLine("\n-- Q6: per-process startup cost");

        var samples = new List<double>();

        for (var i = 0; i < 5; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            await using var session = NewSession();
            await session.Connect();
            samples.Add(session.StartupCost.TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];

        _report.Add(_name, "Q6 — process model: startup cost", "launch + handshake", Verdict.Yes,
            $"median {median:F0}ms (min {samples[0]:F0}ms, max {samples[^1]:F0}ms) over {samples.Count} launches");
    }

    private static string Short(string s, int max = 60)
    {
        var name = s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..] : s;
        return Clip(name, max);
    }

    /// <summary>Truncates without splitting — uids are opaque and must not be shortened at dots.</summary>
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
