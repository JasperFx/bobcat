using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#572: <c>bobcat run</c> always leaves <c>obj/bobcat/last-run.json</c> behind, and
/// <c>--failed</c> / <c>--repeat</c> run from it. Driven through the <c>bobcat</c> executable
/// against <c>Bobcat.Supervisor.ProjectedWorker</c>, whose <c>Basics/always fails</c> is red on
/// purpose — the file is the tool's, so a class nothing ships would prove nothing.
/// </summary>
/// <remarks>
/// One class, so its tests run one at a time: they share the worker project's state file. The
/// supervised runs of bobcat#546 (<c>--retries</c>, <c>--workers</c>, <c>--stall</c>) are here for
/// the same reason.
/// </remarks>
public class RunFailedAndRepeatTests : IDisposable
{
    /// <summary>The <c>bobcat</c> tool, whose assembly is <c>Bobcat.Cli</c> (see Bobcat.Console.csproj).</summary>
    private static readonly string tool = Path.Combine(
        Path.GetDirectoryName(WorkerHosts.Locate("Bobcat.Console"))!,
        OperatingSystem.IsWindows() ? "Bobcat.Cli.exe" : "Bobcat.Cli");

    private static readonly string configuration = Path.GetFileName(
        Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

    private static readonly string project = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(WorkerHosts.ProjectedWorker)!, "..", "..", "..", "Bobcat.Supervisor.ProjectedWorker.csproj"));

    private static readonly string lastRun = Path.Combine(Path.GetDirectoryName(project)!, "obj", "bobcat", "last-run.json");

    public RunFailedAndRepeatTests() => forget();

    public void Dispose() => forget();

    private static void forget()
    {
        if (File.Exists(lastRun)) File.Delete(lastRun);
    }

    private static Task<(int ExitCode, string Output, string Error)> bobcat(params string[] arguments)
        => bobcat(new Dictionary<string, string>(), arguments);

    private static async Task<(int ExitCode, string Output, string Error)> bobcat(
        IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in (string[])["run", project, "--no-build", "--configuration", configuration, .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["BOBCAT_MONITOR"] = "0";
        foreach (var (key, value) in environment) start.Environment[key] = value;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await output, await error);
    }

    private static JsonNode recorded() => JsonNode.Parse(File.ReadAllText(lastRun))!;

    private static string[] strings(JsonNode? array) => array!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    private static string[] scenariosIn(string resultsJson)
    {
        using var document = JsonDocument.Parse(resultsJson);
        return document.RootElement.GetProperty("scenarios").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()!)
            .ToArray();
    }

    [Fact]
    public async Task a_run_records_what_failed_and_failed_reruns_exactly_that()
    {
        var first = await bobcat("--feature", "Basics");
        first.ExitCode.ShouldNotBe(0, first.Output);

        var state = recorded();
        strings(state["failed"]).ShouldBe(["Basics/always fails"]);
        state["resultsRead"]!.GetValue<bool>().ShouldBeTrue();
        state["exitCode"]!.GetValue<int>().ShouldBe(2);
        strings(state["arguments"]!["selection"]).ShouldContain("Basics/passes");
        strings(state["arguments"]!["selection"]).ShouldNotContain(x => !x.StartsWith("Basics/"));

        // Reserved for seeded order (C8): present now so --repeat needs no format change later
        state.AsObject().ContainsKey("seed").ShouldBeTrue();

        var rerun = await bobcat("--failed", "--json");
        rerun.ExitCode.ShouldNotBe(0, rerun.Error);
        scenariosIn(rerun.Output).ShouldBe(["Basics/always fails"]);

        // The rerun is itself the last run now, narrowed to what it ran
        strings(recorded()["arguments"]!["selection"]).ShouldBe(["Basics/always fails"]);
    }

    [Fact]
    public async Task repeat_replays_the_selection_and_flags()
    {
        var first = await bobcat("--feature", "Basics", "--scenario", "also passes", "--json");
        first.ExitCode.ShouldBe(0, first.Error);
        strings(recorded()["failed"]).ShouldBeEmpty();

        // --json is replayed too: standard output is the results file again
        var again = await bobcat("--repeat");
        again.ExitCode.ShouldBe(0, again.Error);
        scenariosIn(again.Output).ShouldBe(["Basics/also passes"]);
    }

    /// <summary>
    /// bobcat#541: <c>--html</c> writes the page from the run's results, and is replayed by
    /// <c>--repeat</c>. The page is a template only a tool packed with EmbedFrontend carries, so a
    /// plain build says why there is no page instead — and either way the run's verdict is the
    /// specifications', never the page's. ui-smoke.yml opens a page the packed tool wrote.
    /// </summary>
    [Fact]
    public async Task html_writes_the_results_page_or_says_why_it_cannot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bobcat-run-html", Guid.NewGuid().ToString("N"));
        var page = Path.Combine(directory, "results.html");
        try
        {
            var run = await bobcat("--feature", "Basics", "--scenario", "also passes", "--html", page);
            run.ExitCode.ShouldBe(0, run.Output);

            if (File.Exists(page))
            {
                File.ReadAllText(page).ShouldContain("Basics");
            }
            else
            {
                string.Join(' ', run.Output.Split((char[])['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries))
                    .ShouldContain("built without its web pages");
            }

            recorded()["arguments"]!["html"]!.GetValue<string>().ShouldBe(page);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task an_identity_the_suite_no_longer_has_is_refused_by_name()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lastRun)!);
        File.WriteAllText(lastRun, """
            {
              "version": 1,
              "timestamp": "2026-10-16T00:00:00+00:00",
              "exitCode": 2,
              "resultsRead": true,
              "failed": [ "Basics/always fails", "Basics/renamed since" ],
              "arguments": { "framework": "net10.0", "configuration": "Debug", "selection": [] },
              "seed": null
            }
            """);

        var refused = await bobcat("--failed");

        refused.ExitCode.ShouldNotBe(0);
        refused.Output.ShouldContain("Basics/renamed since");
        refused.Output.ShouldNotContain("Specifications failed");

        // Nothing ran, so nothing was recorded over it
        strings(recorded()["failed"]).ShouldContain("Basics/renamed since");
    }

    [Fact]
    public async Task nothing_failed_says_so_and_exits_zero()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lastRun)!);
        File.WriteAllText(lastRun, """
            { "version": 1, "timestamp": "2026-10-16T00:00:00+00:00", "exitCode": 0, "resultsRead": true,
              "failed": [], "arguments": { "selection": [] }, "seed": null }
            """);

        var result = await bobcat("--failed");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Nothing failed");
    }

    [Fact]
    public async Task failed_with_no_last_run_is_refused()
    {
        var result = await bobcat("--failed");

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("no last run");
    }

    private static Dictionary<string, string> flakyState(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"bobcat-flaky-{Guid.NewGuid():N}");
        return new Dictionary<string, string> { ["BOBCAT_FLAKY_STATE"] = path };
    }

    [Fact]
    public async Task retries_makes_the_run_supervised_and_a_pass_on_retry_is_not_a_failure()
    {
        var environment = flakyState(out var state);
        try
        {
            var run = await bobcat(environment, "--scenario", "flaky until second attempt", "--retries", "2");

            run.ExitCode.ShouldBe(0, run.Output);
            run.Output.ShouldContain("1 passed on retry");
            run.Output.ShouldContain("Passed on retry (not clean passes)");

            // The first attempt's results file says it failed; the retry's says it passed, and wins
            strings(recorded()["failed"]).ShouldBeEmpty();
            recorded()["arguments"]!["retries"]!.GetValue<int>().ShouldBe(2);
        }
        finally
        {
            File.Delete(state);
        }
    }

    [Fact]
    public async Task without_retries_a_retry_trait_retries_nothing()
    {
        var environment = flakyState(out var state);
        try
        {
            var run = await bobcat(environment, "--scenario", "flaky until second attempt");

            run.ExitCode.ShouldNotBe(0, run.Output);
            strings(recorded()["failed"]).ShouldBe(["Fussy/flaky until second attempt"]);
            recorded()["arguments"]!["retries"].ShouldBeNull();
        }
        finally
        {
            File.Delete(state);
        }
    }

    [Fact]
    public async Task workers_runs_the_selection_in_lanes_and_repeat_replays_it()
    {
        var run = await bobcat("--feature", "Basics", "--workers", "2", "--stall", "90s");

        run.ExitCode.ShouldNotBe(0, run.Output);
        run.Output.ShouldContain("3 passed, 1 failed");
        strings(recorded()["failed"]).ShouldBe(["Basics/always fails"]);

        var arguments = recorded()["arguments"]!;
        arguments["workers"]!.GetValue<int>().ShouldBe(2);
        arguments["stall"]!.GetValue<string>().ShouldBe("90s");
        arguments["stallAction"]!.GetValue<string>().ShouldBe("report");

        var again = await bobcat("--repeat");
        again.ExitCode.ShouldNotBe(0, again.Output);
        again.Output.ShouldContain("3 passed, 1 failed");
        recorded()["arguments"]!["workers"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task a_stall_is_named_and_abort_stops_the_run()
    {
        var run = await bobcat(new Dictionary<string, string> { ["BOBCAT_HANG"] = "true" },
            "--scenario", "hangs when armed", "--stall", "2s", "--stall-action", "abort");

        run.ExitCode.ShouldNotBe(0, run.Output);
        run.Output.ShouldContain("Stalled (in flight past the stall threshold)");
        run.Output.ShouldContain("The run was aborted");
    }

    /// <summary>
    /// The features each results file in <paramref name="directory"/> holds, in the order the files'
    /// brackets started. A supervised worker writes one file per request, so under a random order
    /// (one request per class) this is the order the classes ran in — the rendered console sorts
    /// by feature and says nothing about it.
    /// </summary>
    private static string[] featuresByStart(string directory)
        => Directory.EnumerateFiles(directory, "*.json")
            .Where(f => !f.EndsWith(".schema.json", StringComparison.Ordinal))
            .Select(f =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(f));
                var run = document.RootElement.GetProperty("run");
                var features = document.RootElement.GetProperty("scenarios").EnumerateArray()
                    .Select(s => s.GetProperty("feature").GetString()!).Distinct().Single();
                return (StartedAt: run.GetProperty("startedAt").GetDateTimeOffset(), Feature: features,
                    Order: run.TryGetProperty("order", out var o) ? o.GetString() : null,
                    Seed: run.TryGetProperty("seed", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : (int?)null);
            })
            .OrderBy(x => x.StartedAt)
            .Select(x => $"{x.Feature} ({x.Order}, {x.Seed})")
            .ToArray();

    /// <summary>
    /// bobcat#578: a random order runs the classes in the seed's order (one request per class, so the
    /// framework's own collection order — <c>Basics</c> before <c>Fussy</c> in this worker — does not
    /// decide), says its seed, stamps it on every worker's results file, and <c>--repeat</c> runs the
    /// same order from <c>last-run.json</c>.
    /// </summary>
    [Fact]
    public async Task order_random_runs_the_classes_in_the_seeds_order_and_repeat_reproduces_it()
    {
        // Two classes, three scenarios: "passes" matches Basics/passes and Basics/also passes. One
        // --scenario with two values: a second --scenario flag replaces the first in JasperFx's parser.
        string[] selection = ["--scenario", "passes", "flaky until second attempt"];

        // A seed that puts Fussy first: the worker's own orderer always runs Basics first, so this
        // is the case only the supervisor's per-class requests can produce.
        var seed = Enumerable.Range(1, 100).First(s => WorkPlan.Shuffle(["Basics", "Fussy"], s)[0] == "Fussy");
        var basicsFirst = Enumerable.Range(1, 100).First(s => WorkPlan.Shuffle(["Basics", "Fussy"], s)[0] == "Basics");

        var results = Path.Combine(Path.GetTempPath(), "bobcat-run-order", Guid.NewGuid().ToString("N"));
        try
        {
            var run = await bobcat([.. selection, "--order", "random", "--seed", seed.ToString(), "--results", Path.Combine(results, "first")]);
            run.ExitCode.ShouldBe(0, run.Output);
            run.Output.ShouldContain($"Order: random, seed {seed}");
            run.Output.ShouldContain("3 passed");
            featuresByStart(Path.Combine(results, "first")).ShouldBe([$"Fussy (random, {seed})", $"Basics (random, {seed})"]);

            recorded()["seed"]!.GetValue<int>().ShouldBe(seed);
            recorded()["arguments"]!["order"]!.GetValue<string>().ShouldBe("random");

            var again = await bobcat("--repeat", "--results", Path.Combine(results, "again"));
            again.ExitCode.ShouldBe(0, again.Output);
            again.Output.ShouldContain($"Order: random, seed {seed}");
            featuresByStart(Path.Combine(results, "again")).ShouldBe([$"Fussy (random, {seed})", $"Basics (random, {seed})"]);
            recorded()["seed"]!.GetValue<int>().ShouldBe(seed);

            // The other way round for a seed that keeps Basics first, so the order is the seed's and not an accident.
            var reversed = await bobcat([.. selection, "--order", "random", "--seed", basicsFirst.ToString(), "--results", Path.Combine(results, "reversed")]);
            reversed.ExitCode.ShouldBe(0, reversed.Output);
            featuresByStart(Path.Combine(results, "reversed")).ShouldBe([$"Basics (random, {basicsFirst})", $"Fussy (random, {basicsFirst})"]);

            // And a declared run is one request, one file, with no order of its own.
            var declared = await bobcat([.. selection, "--workers", "1", "--results", Path.Combine(results, "declared")]);
            declared.ExitCode.ShouldBe(0, declared.Output);
            Directory.EnumerateFiles(Path.Combine(results, "declared"), "*.json").Count(f => !f.EndsWith(".schema.json")).ShouldBe(1);
            using var document = JsonDocument.Parse(File.ReadAllText(Directory.EnumerateFiles(Path.Combine(results, "declared"), "*.json").Single(f => !f.EndsWith(".schema.json"))));
            document.RootElement.GetProperty("run").TryGetProperty("order", out var order).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(results)) Directory.Delete(results, recursive: true);
        }
    }

    [Fact]
    public async Task order_random_without_a_seed_mints_one_and_records_it()
    {
        var run = await bobcat("--scenario", "also passes", "--order", "random");

        run.ExitCode.ShouldBe(0, run.Output);
        run.Output.ShouldContain("Order: random, seed ");

        var seed = recorded()["seed"]!.GetValue<int>();
        run.Output.ShouldContain($"seed {seed}");
        recorded()["arguments"]!["order"]!.GetValue<string>().ShouldBe("random");
    }

    [Fact]
    public async Task a_declared_run_records_no_seed_and_no_order()
    {
        var run = await bobcat("--scenario", "also passes", "--workers", "1");

        run.ExitCode.ShouldBe(0, run.Output);
        run.Output.ShouldNotContain("Order:");
        recorded()["seed"].ShouldBeNull();
        recorded()["arguments"]!["order"].ShouldBeNull();
    }

    [Theory]
    [InlineData("--stall", "soon", "is not a duration")]
    [InlineData("--stall-action", "explode", "is not one of report, kill-and-retry, abort")]
    [InlineData("--workers", "0", "--workers must be 1 or more")]
    [InlineData("--order", "sideways", "--order 'sideways' is not one of declared, random")]
    [InlineData("--seed", "5", "--seed needs --order random")]
    [InlineData("--shard", "2of4", "--shard '2of4' is not a shard")]
    [InlineData("--shard", "0/4", "the leg must be between 1 and 4")]
    [InlineData("--shard", "5/4", "the leg must be between 1 and 4")]
    [InlineData("--shard", "1/0", "the number of legs must be 1 or more")]
    public async Task a_bad_supervision_flag_is_refused_before_anything_runs(string flag, string value, string message)
    {
        var refused = await bobcat(flag, value);

        refused.ExitCode.ShouldNotBe(0);
        refused.Output.ShouldContain(message);
        File.Exists(lastRun).ShouldBeFalse();
    }

    /// <summary>
    /// The features each results file in <paramref name="directory"/> holds, as a sorted set.
    /// </summary>
    private static string[] featuresIn(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json")
                .Where(f => !f.EndsWith(".schema.json", StringComparison.Ordinal))
                .SelectMany(f =>
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(f));
                    document.RootElement.GetProperty("run").GetProperty("shard").GetString().ShouldNotBeNull();
                    return document.RootElement.GetProperty("scenarios").EnumerateArray()
                        .Select(s => s.GetProperty("feature").GetString()!).ToList();
                })
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    /// <summary>
    /// bobcat#576: two legs of one selection run disjoint classes that between them are all of it,
    /// each says which leg it was (console, report, results file, last-run.json), and <c>--repeat</c>
    /// runs the same leg. With two classes, a third leg has nothing to run, and that passes.
    /// </summary>
    [Fact]
    public async Task two_shards_cover_every_class_once_and_repeat_replays_the_leg()
    {
        // Basics holds two of these scenarios and Fussy one, so a count balance puts Basics on leg 1.
        string[] selection = ["--scenario", "passes", "flaky until second attempt"];
        var results = Path.Combine(Path.GetTempPath(), "bobcat-run-shard", Guid.NewGuid().ToString("N"));
        try
        {
            var first = await bobcat([.. selection, "--shard", "1/2", "--results", Path.Combine(results, "1")]);
            first.ExitCode.ShouldBe(0, first.Output);
            first.Output.ShouldContain("Shard 1/2: 1 of 2 class(es) (2 of 3 test(s)), legs balanced by test count");
            recorded()["arguments"]!["shard"]!.GetValue<string>().ShouldBe("1/2");

            var second = await bobcat([.. selection, "--shard", "2/2", "--results", Path.Combine(results, "2")]);
            second.ExitCode.ShouldBe(0, second.Output);
            second.Output.ShouldContain("Shard 2/2: 1 of 2 class(es) (1 of 3 test(s))");

            featuresIn(Path.Combine(results, "1")).ShouldBe(["Basics"]);
            featuresIn(Path.Combine(results, "2")).ShouldBe(["Fussy"]);

            // The leg recorded last was 2/2; --repeat runs exactly that leg again.
            var again = await bobcat("--repeat", "--results", Path.Combine(results, "again"));
            again.ExitCode.ShouldBe(0, again.Output);
            again.Output.ShouldContain("Shard 2/2:");
            featuresIn(Path.Combine(results, "again")).ShouldBe(["Fussy"]);

            // More legs than classes: the third has nothing to run, which is the split working.
            var third = await bobcat([.. selection, "--shard", "3/3", "--results", Path.Combine(results, "3")]);
            third.ExitCode.ShouldBe(0, third.Output);
            third.Output.ShouldContain("Shard 3/3: 0 of 2 class(es) (0 of 3 test(s))");
            featuresIn(Path.Combine(results, "3")).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(results)) Directory.Delete(results, recursive: true);
        }
    }

    [Theory]
    [InlineData("--failed", "--failed reruns what failed in the last run")]
    [InlineData("--repeat", "--repeat replays the last run's --shard")]
    public async Task shard_is_refused_with_failed_and_repeat(string flag, string message)
    {
        var refused = await bobcat(flag, "--shard", "1/2");

        refused.ExitCode.ShouldNotBe(0);
        refused.Output.ShouldContain(message);
        File.Exists(lastRun).ShouldBeFalse();
    }

    [Fact]
    public async Task a_missing_ledger_balances_by_count_and_is_recorded()
    {
        var ledger = Path.Combine(Path.GetTempPath(), "bobcat-run-ledger", Guid.NewGuid().ToString("N"), "ledger.json");

        var run = await bobcat("--scenario", "also passes", "--ledger", ledger);

        run.ExitCode.ShouldBe(0, run.Output);
        run.Output.ShouldContain("balanced by test count");
        recorded()["arguments"]!["ledger"]!.GetValue<string>().ShouldBe(ledger);
    }

    [Fact]
    public async Task an_unreadable_ledger_is_refused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bobcat-run-ledger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ledger = Path.Combine(directory, "ledger.json");
        await File.WriteAllTextAsync(ledger, "this is not a ledger");
        try
        {
            var run = await bobcat("--scenario", "also passes", "--ledger", ledger);

            run.ExitCode.ShouldNotBe(0);
            run.Output.ShouldContain("could not be read");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task json_is_refused_for_a_supervised_run()
    {
        var refused = await bobcat("--workers", "2", "--json");

        refused.ExitCode.ShouldNotBe(0);
        refused.Error.ShouldContain("--results <dir>");
        File.Exists(lastRun).ShouldBeFalse();
    }
}
