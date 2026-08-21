using JasperFx.Testing;
using System.Reflection;
using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Rendering;
using Bobcat.Resilience;

namespace Bobcat.Runtime;

/// <summary>
/// Entry point for running Bobcat specs from a Program.cs.
/// Discovers generated FeatureDefinitions, manages resources, executes scenarios.
/// </summary>
public class BobcatRunner
{
    private readonly TestSuite _suite = new();
    private readonly List<FeatureDefinition> _features = new();
    private readonly CommandLineRenderer _renderer = new();
    private readonly List<IFailurePolicy> _policies = new();
    private IExecutionObserver _observer = NullObserver.Instance;

    public TestSuite Suite => _suite;

    /// <summary>
    /// Registered features. Exposed so a front-end can enumerate the run without executing it —
    /// what an MTP host does for <c>--list-tests</c> and IDE Test Explorer discovery.
    /// </summary>
    public IReadOnlyList<FeatureDefinition> Features => _features;

    /// <summary>
    /// Narrows the run to specific scenarios, on top of the feature/tag filters. An MTP host
    /// sets this from the platform's uid filter so a supervisor can re-run exactly one scenario.
    /// </summary>
    public Func<FeatureDefinition, ScenarioDefinition, bool>? ScenarioFilter { get; set; }

    /// <summary>
    /// Checks run once before any feature. A failure aborts the run in seconds rather than
    /// producing thousands of identical downstream failures.
    /// </summary>
    public Preflight Preflight { get; } = new();

    /// <summary>
    /// Caps how much retrying this run may do. Defaults to <see cref="RetryBudget.None"/> —
    /// retries are opt-in, so an unconfigured run behaves exactly as it did before.
    /// </summary>
    public RetryBudget RetryBudget { get; set; } = RetryBudget.None;

    /// <summary>
    /// Registered policies, tried in order; <see cref="DefaultFailurePolicy"/> always decides
    /// last so a custom policy can abstain on cases it does not care about.
    /// </summary>
    public BobcatRunner AddFailurePolicy(IFailurePolicy policy)
    {
        _policies.Add(policy);
        return this;
    }

    /// <summary>
    /// Author-declared knowledge about which failures recover how — issue #44 layer 1. Populated
    /// from <see cref="RecoveryHintAttribute"/>s on fixtures as features are added, and open for
    /// hints added directly.
    /// </summary>
    public RecoveryHintSet RecoveryHints { get; } = new();

    private IFailurePolicy policy => new FailurePolicyChain(
        [.. _policies, new HintedFailurePolicy(RecoveryHints), new DefaultFailurePolicy()]);
    /// <summary>
    /// Skip the live Spectre.Console rendering. Set for JSON output, and by tests that assert
    /// on <see cref="SuiteResults"/> rather than on what the terminal showed.
    /// </summary>
    public bool SuppressConsoleOutput { get; set; }

    /// <summary>
    /// Replaces the current observer. Prefer <see cref="AddObserver"/> when the intent is to
    /// watch the run in addition to whatever a front-end already registered.
    /// </summary>
    public BobcatRunner WithObserver(IExecutionObserver observer)
    {
        _observer = observer;
        return this;
    }

    /// <summary>
    /// Attaches an observer WITHOUT displacing the existing one — additional observers fan in
    /// via <see cref="CompositeObserver"/>, so an MTP host's node publisher and a monitor
    /// publisher can watch the same run.
    /// </summary>
    public BobcatRunner AddObserver(IExecutionObserver observer)
    {
        _observer = _observer switch
        {
            NullObserver => observer,
            CompositeObserver composite => new CompositeObserver([.. composite.Observers, observer]),
            _ => new CompositeObserver(_observer, observer)
        };
        return this;
    }

    /// <summary>
    /// Opt-in publishing of live progress to a locally running Bobcat.Console host. Off by
    /// default so unit tests driving BobcatRunner directly never probe or publish; the real
    /// entry points (<see cref="Run"/>, the MTP host) turn it on. Even when on, an absent
    /// monitor costs exactly one refused local connection — see <see cref="MonitorPublisher"/>.
    /// </summary>
    public bool PublishToMonitor { get; set; }

    /// <summary>How this run is hosted, as reported to the monitor ("in-process", "mtp-host").</summary>
    public string MonitorMode { get; set; } = "in-process";

    /// <summary>
    /// Register a feature definition (typically from generated code).
    /// </summary>
    public BobcatRunner AddFeature(FeatureDefinition feature)
    {
        _features.Add(feature);

        // A fixture owns exactly one feature, so its hints scope to that feature by test-id
        // prefix — the same "{Feature}/{Scenario}" identity the retry budget and MTP host use.
        RecoveryHints.AddFromType(feature.FixtureType, $"{feature.Title}/");

        return this;
    }

    /// <summary>
    /// Scan an assembly for all generated FeatureDefinition factories.
    /// Looks for static classes with a public static Define() method returning FeatureDefinition.
    /// </summary>
    public BobcatRunner ScanForFeatures(Assembly assembly)
    {
        // Assembly-level hints are the run-wide defaults a fixture can then override.
        RecoveryHints.AddFromAssembly(assembly);

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || !type.IsAbstract || !type.IsSealed) continue; // static classes

            var defineMethod = type.GetMethod("Define", BindingFlags.Public | BindingFlags.Static);
            if (defineMethod == null || defineMethod.ReturnType != typeof(FeatureDefinition)) continue;

            var feature = (FeatureDefinition?)defineMethod.Invoke(null, null);
            if (feature != null)
            {
                AddFeature(feature);
            }
        }

        return this;
    }

    /// <summary>
    /// Run all features matching the optional filters.
    /// </summary>
    public async Task<SuiteResults> RunAll(
        string? featureFilter = null,
        string? tagFilter = null)
    {
        var suiteResults = new SuiteResults();

        var features = filteredFeatures(featureFilter).ToArray();
        var monitor = await tryAttachMonitor();

        try
        {
            _observer.RunStarted(features.Sum(f => filteredScenarios(f, tagFilter).Count()));

            await _suite.StartAll();

            // Preflight runs with resources up but before any feature: checking a database means
            // little until the resource that owns the connection has started.
            var preflight = await runPreflight();
            if (preflight is not null)
            {
                await _suite.DisposeAsync();
                suiteResults.PreflightFailure = preflight;
                return suiteResults;
            }

            try
            {
                // Global actions run with resources up but before any feature, and tear down
                // after the last feature but before resources are disposed.
                await _suite.RunGlobalSetUp();

                try
                {
                    foreach (var feature in features)
                    {
                        var featureResults = await runFeature(feature, tagFilter);
                        suiteResults.Add(featureResults);

                        // Stop on catastrophic
                        if (featureResults.WasCatastrophic) break;
                    }
                }
                finally
                {
                    await _suite.RunGlobalTearDown();
                }
            }
            finally
            {
                await _suite.DisposeAsync();
            }

            return suiteResults;
        }
        finally
        {
            // Fires on the preflight-failure path too — a run that never got going is still a
            // run the monitor saw start.
            _observer.RunFinished(suiteResults);
            if (monitor != null) await monitor.DisposeAsync();
        }
    }

    /// <summary>
    /// When <see cref="PublishToMonitor"/> is on and a monitor answers the ping, attaches a
    /// publishing observer beside whatever is already registered. Returns the observer so
    /// <see cref="RunAll"/> can flush and dispose it when the run closes out.
    /// </summary>
    private async Task<MonitorPublishingObserver?> tryAttachMonitor()
    {
        if (!PublishToMonitor) return null;

        var publisher = await MonitorPublisher.TryConnect();
        if (publisher == null) return null;

        var observer = new MonitorPublishingObserver(
            publisher, MonitorRunInfo.Discover(MonitorMode), ownedPublisher: publisher);
        AddObserver(observer);
        return observer;
    }

    private IEnumerable<FeatureDefinition> filteredFeatures(string? featureFilter)
        => featureFilter == null
            ? _features
            : _features.Where(f => f.Title.Contains(featureFilter, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The one place the tag and scenario filters are applied, so the up-front total the
    /// monitor sees and what <see cref="runFeature"/> actually runs can never disagree.
    /// </summary>
    private IEnumerable<ScenarioDefinition> filteredScenarios(FeatureDefinition feature, string? tagFilter)
    {
        var scenarios = feature.Scenarios.AsEnumerable();

        if (tagFilter != null)
        {
            scenarios = scenarios.Where(s =>
                s.Tags.Any(t => t.Equals(tagFilter, StringComparison.OrdinalIgnoreCase)));
        }

        if (ScenarioFilter != null)
        {
            scenarios = scenarios.Where(s => ScenarioFilter(feature, s));
        }

        return scenarios;
    }

    /// <summary>Returns a description of the failure, or null when the environment is fine.</summary>
    private async Task<string?> runPreflight()
    {
        Preflight.AddResourceChecks(_suite.Resources);
        if (Preflight.IsEmpty) return null;

        var results = await Preflight.Run();
        if (results.Succeeded()) return null;

        var description = Runtime.Preflight.Describe(results);
        if (!SuppressConsoleOutput) Console.WriteLine(description);

        return description;
    }

    private async Task<FeatureResults> runFeature(FeatureDefinition feature, string? tagFilter)
    {
        var featureResults = new FeatureResults(feature.Title);
        if (!SuppressConsoleOutput) _renderer.RenderFeatureHeader(feature.Title);
        _observer.FeatureStarted(feature.Title);

        var scenarios = filteredScenarios(feature, tagFilter);

        // BeforeAll/AfterAll run once per feature, outside any scenario scope. They get a
        // feature-level context so they can reach resources and root services — asking it
        // for a scoped service throws, which is the intended rejection.
        var featureContext = new SpecExecutionContext(feature.Title, suite: _suite);
        if (feature.BeforeAll != null) await feature.BeforeAll(featureContext);

        try
        {
            foreach (var scenario in scenarios)
            {
                var result = await runScenarioWithRetries(feature, scenario);

                featureResults.Add(result);
                _observer.ScenarioCompleted(feature.Title, result);

                // Render immediately (unless suppressed for JSON mode)
                if (!SuppressConsoleOutput)
                {
                    var specRender = SpecRender.FromResults(scenario.Title, result.Results, feature.Title);
                    _renderer.Render(specRender);
                    _renderer.RenderRetrySummary(result);
                }

                // Stop feature on catastrophic, or when a policy said to abort the run
                if (result.Outcome == RunOutcome.Aborted ||
                    result.Results.Steps.Any(s => s.FailureLevel == FailureLevel.Catastrophic))
                    break;
            }
        }
        finally
        {
            if (feature.AfterAll != null) await feature.AfterAll(featureContext);
        }

        _observer.FeatureFinished(feature.Title);
        return featureResults;
    }

    /// <summary>
    /// Runs one scenario, consulting the failure policy after each attempt and retrying while it
    /// asks for one and the budget allows it.
    /// </summary>
    /// <remarks>
    /// Every attempt — including retries — gets the full <c>ResetAll</c> → <c>BeginScenarioAll</c>
    /// → <c>EndScenarioAll</c> bracket. A retry that reused dirty state from the failed attempt
    /// would be testing something other than the scenario.
    /// </remarks>
    private async Task<ScenarioResult> runScenarioWithRetries(FeatureDefinition feature, ScenarioDefinition scenario)
    {
        var traits = ResilienceTags.ToTraits(scenario.Tags);
        var testId = $"{feature.Title}/{scenario.Title}";
        var policy = this.policy;
        var attempts = new List<AttemptRecord>();

        for (var attemptNumber = 1; ; attemptNumber++)
        {
            // ResetAll stays BEFORE the scope opens: clean persistent state (DB rows, queues),
            // then open a fresh DI scope over it.
            await _suite.ResetAll();
            await _suite.BeginScenarioAll();

            ScenarioResult result;
            try
            {
                result = await runScenario(feature, scenario);
            }
            finally
            {
                await _suite.EndScenarioAll();
            }

            var succeeded = result.Results.Counts.Succeeded;

            var decided = policy.Decide(new AttemptContext
            {
                TestId = testId,
                Title = scenario.Title,
                AttemptNumber = attemptNumber,
                Succeeded = succeeded,
                FailureLevel = worstFailureLevel(result.Results),
                Exception = result.Results.AllExceptions().FirstOrDefault(),
                Traits = traits,
                RetriesAvailable = RetryBudget.CanRetry(testId, traits),
                AttemptsAllowed = RetryBudget.AttemptsAllowedFor(traits)
            }) ?? Disposition.FailAndContinue("failed");

            var (effective, unsupported, willRetry) = resolve(decided, testId, traits);

            attempts.Add(new AttemptRecord(attemptNumber, succeeded, effective) { Unsupported = unsupported });

            if (!willRetry)
            {
                return new ScenarioResult(scenario.Title, scenario.Tags, result.Results) { Attempts = attempts };
            }

            _observer.ScenarioRetrying(scenario.Title, attemptNumber + 1, effective.Reason);
            if (!SuppressConsoleOutput) _renderer.RenderRetryNotice(scenario.Title, attemptNumber + 1, effective.Reason);
        }
    }

    /// <summary>
    /// Turns a policy's wish into what will actually happen. Two things can stand in the way:
    /// the retry budget, and dispositions that need the out-of-process supervisor. Neither is
    /// silently downgraded — the reason is recorded on the attempt and surfaced in the report,
    /// because a run report that implies a retry happened when it did not is worse than no
    /// report at all.
    /// </summary>
    private (Disposition Effective, string? Unsupported, bool WillRetry) resolve(
        Disposition decided, string testId, IReadOnlyDictionary<string, string> traits)
    {
        if (!decided.IsRetry) return (decided, null, false);

        if (decided.RequiresSupervisor)
        {
            var step = decided.Kind == DispositionKind.RetryInFreshProcess
                ? "the supervisor/worker process split"
                : "supervisor-owned recyclable resources";

            return (decided,
                $"{decided.Kind} needs {step}, which is not built yet — this attempt was NOT retried",
                false);
        }

        if (RetryBudget.TryConsume(testId, traits, out var denial))
        {
            return (decided, null, true);
        }

        return (Disposition.FailAndContinue($"a retry was requested ({decided.Reason}) but {denial}"), null, false);
    }

    private static FailureLevel worstFailureLevel(ExecutionResults results)
    {
        var worst = FailureLevel.None;
        foreach (var step in results.Steps)
        {
            if (step.FailureLevel > worst) worst = step.FailureLevel;
        }
        return worst;
    }

    private async Task<ScenarioResult> runScenario(FeatureDefinition feature, ScenarioDefinition scenario)
    {
        var fixture = (Fixture)Activator.CreateInstance(feature.FixtureType)!;

        var timeout = SpecTags.GetTimeout(scenario.Tags) ?? TimeSpan.FromSeconds(30);
        var plan = new ExecutionPlan(scenario.Title, timeout);
        scenario.BuildPlan(fixture, plan);

        var context = new SpecExecutionContext(scenario.Title, suite: _suite);
        fixture.Context = context;

        // Fresh controllable clock per scenario so time-travel never leaks between scenarios.
        Engine.BobcatClock.ResetToControllable();

        // The plan is already built, so the step count is a fact rather than an estimate — a
        // watcher can render "step 3 of 9" from the first step on.
        _observer.ScenarioStarted(feature.Title, scenario.Title, plan.Steps.Count);

        // BeforeEach/AfterEach run inside the scenario's DI scope (opened by the caller), so
        // they inject the same scoped services the steps see.
        if (feature.BeforeEach != null) await feature.BeforeEach(fixture, context);

        try
        {
            var executor = new Executor([new FailureLevelContinuationRule()], _observer);
            await executor.Execute(plan, context);
        }
        finally
        {
            if (feature.AfterEach != null) await feature.AfterEach(fixture, context);
        }

        _observer.ScenarioFinished(context.Results);

        return new ScenarioResult(scenario.Title, scenario.Tags, context.Results);
    }

    /// <summary>
    /// List all discovered features and scenarios.
    /// </summary>
    public void ListFeatures()
    {
        foreach (var feature in _features)
        {
            Console.WriteLine($"Feature: {feature.Title}");
            Console.WriteLine($"  Fixture: {feature.FixtureType.Name}");
            foreach (var scenario in feature.Scenarios)
            {
                var tags = scenario.Tags.Length > 0
                    ? " " + string.Join(" ", scenario.Tags.Select(t => $"@{t}"))
                    : "";
                Console.WriteLine($"  - {scenario.Title}{tags}");
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Print summary counts for the suite run.
    /// </summary>
    public void RenderSummary(SuiteResults results)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════");
        _renderer.RenderCounts(results.Counts);

        var total = results.Features.SelectMany(f => f.Scenarios).Count();
        var passed = results.Features.SelectMany(f => f.Scenarios).Count(s => s.Results.Counts.Succeeded);
        Console.WriteLine($"  {passed}/{total} scenarios passed");

        // Clean-pass and pass-on-retry are reported as different facts, never merged.
        _renderer.RenderResilienceSummary(results);
    }

    /// <summary>
    /// Detects the top-level-statements footgun: when SpecsRunner.cs uses top-level
    /// statements AND project-references a host that also does, both compilations synthesize
    /// a global-namespace 'Program'. AlbaResource&lt;Program&gt; then binds to the wrong entry
    /// point and Alba crashes natively (PAL_SEHException, no managed stack). We turn that into
    /// a clear managed error before any resource starts.
    /// </summary>
    internal static void GuardAgainstProgramCollision()
    {
        var entry = Assembly.GetEntryAssembly();
        var entryHasProgram = entry?.GetType("Program") != null;

        var others = new List<(string Name, bool HasProgram)>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly == entry || assembly.IsDynamic) continue;
            bool hasProgram;
            try { hasProgram = assembly.GetType("Program") != null; }
            catch { continue; } // ignore assemblies that can't be reflected over
            others.Add((assembly.GetName().Name ?? "?", hasProgram));
        }

        var message = DescribeProgramCollision(entry?.GetName().Name, entryHasProgram, others);
        if (message != null)
            throw new BobcatConfigurationException(message);
    }

    /// <summary>
    /// Pure collision check (testable): returns a diagnostic message when the entry assembly
    /// and another loaded assembly both define a global-namespace 'Program', else null.
    /// </summary>
    internal static string? DescribeProgramCollision(
        string? entryName, bool entryHasProgram, IEnumerable<(string Name, bool HasProgram)> others)
    {
        if (entryName == null || !entryHasProgram) return null;

        foreach (var (name, hasProgram) in others)
        {
            if (!hasProgram) continue;
            return $"Ambiguous 'Program' type: the test runner assembly ('{entryName}') and the host assembly " +
                   $"('{name}') both define a global-namespace 'Program'. This usually means SpecsRunner.cs uses " +
                   "top-level statements, which collide with the host's 'Program' so AlbaResource<Program> binds to " +
                   "the wrong entry point (often a native PAL_SEHException with no managed stack). Fix: make " +
                   "SpecsRunner.cs an explicit 'static class SpecsRunner { static Task Main(string[] args) ... }' " +
                   "rather than top-level statements. See docs/sample-wiring.md.";
        }

        return null;
    }

    // --- Convenience static entry point ---

    /// <summary>
    /// Quick entry point for Program.cs:
    /// await BobcatRunner.Run(args, runner => { ... });
    /// </summary>
    public static async Task<int> Run(string[] args, Action<BobcatRunner> configure)
    {
        var runner = new BobcatRunner
        {
            // A real spec-host entry point (not a unit test driving the runner directly), so
            // publish live progress when a monitor is running on this box.
            PublishToMonitor = true
        };
        configure(runner);

        // configure() typically constructs AlbaResource<Program>, which loads the host
        // assembly — so run the collision guard after it, once both assemblies are present.
        GuardAgainstProgramCollision();

        // Simple arg parsing (will move to JasperFx commands later)
        var command = args.Length > 0 ? args[0] : "run";

        if (command == "list")
        {
            runner.ListFeatures();
            return 0;
        }

        string? featureFilter = null;
        string? tagFilter = null;
        bool jsonOutput = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--feature" && i + 1 < args.Length)
                featureFilter = args[++i];
            if (args[i] == "--tag" && i + 1 < args.Length)
                tagFilter = args[++i];
            if (args[i] == "--json")
                jsonOutput = true;
        }

        runner.SuppressConsoleOutput = jsonOutput;
        var results = await runner.RunAll(featureFilter, tagFilter);

        if (jsonOutput)
        {
            Console.WriteLine(Rendering.JsonRenderer.RenderSuite(results));
        }
        else
        {
            runner.RenderSummary(results);
        }

        return results.ExitCode;
    }
}
