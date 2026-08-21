using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Requests;
using Microsoft.Testing.Platform.TestHost;

namespace Bobcat.Mtp;

/// <summary>
/// Bobcat's spec runner exposed as a Microsoft.Testing.Platform test framework, so
/// <c>dotnet test</c>, IDE Test Explorers and CI see Bobcat scenarios as ordinary tests.
/// </summary>
/// <remarks>
/// One scenario is one MTP test node. Features are not nodes: they are a grouping concept that
/// owns <c>BeforeAll</c>/<c>AfterAll</c>, and making them nodes would imply the platform could
/// schedule them independently, which it cannot.
/// </remarks>
public sealed class BobcatTestFramework : ITestFramework, IDataProducer
{
    private readonly Action<BobcatRunner> _configure;

    public BobcatTestFramework(Action<BobcatRunner> configure, ITestFrameworkCapabilities capabilities)
        => _configure = configure;

    public string Uid => nameof(BobcatTestFramework);
    public string Version => typeof(BobcatTestFramework).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    public string DisplayName => "Bobcat";
    public string Description => "Runs Bobcat specifications as a Microsoft.Testing.Platform test host.";

    public Type[] DataTypesProduced => [typeof(TestNodeUpdateMessage)];

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public Task<CreateTestSessionResult> CreateTestSessionAsync(CreateTestSessionContext context)
        => Task.FromResult(new CreateTestSessionResult { IsSuccess = true });

    public Task<CloseTestSessionResult> CloseTestSessionAsync(CloseTestSessionContext context)
        => Task.FromResult(new CloseTestSessionResult { IsSuccess = true });

    public async Task ExecuteRequestAsync(ExecuteRequestContext context)
    {
        try
        {
            switch (context.Request)
            {
                case DiscoverTestExecutionRequest discover:
                    await this.discover(discover, context);
                    break;

                case RunTestExecutionRequest run:
                    await this.run(run, context);
                    break;
            }
        }
        finally
        {
            context.Complete();
        }
    }

    /// <summary>
    /// Enumerates scenarios without executing anything — and, importantly, without starting
    /// resources. Discovery must not stand up a database or a host: IDEs discover on every
    /// build.
    /// </summary>
    private async Task discover(DiscoverTestExecutionRequest request, ExecuteRequestContext context)
    {
        var runner = buildRunner();

        foreach (var (feature, scenario) in scenarios(runner))
        {
            if (!matches(request.Filter, SpecNodeMapping.Uid(feature, scenario))) continue;

            var properties = new PropertyBag(DiscoveredTestNodeStateProperty.CachedInstance);
            foreach (var trait in SpecNodeMapping.Traits(scenario.Tags)) properties.Add(trait);

            await publish(context, request.Session.SessionUid, new TestNode
            {
                Uid = SpecNodeMapping.Uid(feature, scenario),
                DisplayName = SpecNodeMapping.DisplayName(feature.Title, scenario.Title),
                Properties = properties
            });
        }
    }

    private async Task run(RunTestExecutionRequest request, ExecuteRequestContext context)
    {
        var runner = buildRunner();

        // The platform hands the requested subset down as a filter. Honouring it is what makes
        // a supervisor's selective re-run and [Isolated] scheduling work against this host.
        runner.ScenarioFilter = (feature, scenario) =>
            matches(request.Filter, SpecNodeMapping.Uid(feature, scenario));

        runner.WithObserver(new PublishingObserver(this, context, request.Session.SessionUid));

        // Execution (never discovery — IDEs discover on every build) also streams to a locally
        // running Bobcat.Console when there is one; the probe costs one refused connection
        // otherwise. Attached via AddObserver inside RunAll, so it rides beside the node
        // publisher above.
        runner.PublishToMonitor = true;
        runner.MonitorMode = "mtp-host";

        await runner.RunAll();
    }

    private BobcatRunner buildRunner()
    {
        // The platform owns stdout — Spectre rendering would interleave with its own reporting
        // and corrupt the console output.
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        _configure(runner);
        return runner;
    }

    private static IEnumerable<(FeatureDefinition Feature, ScenarioDefinition Scenario)> scenarios(BobcatRunner runner)
    {
        foreach (var feature in runner.Features)
            foreach (var scenario in feature.Scenarios)
                yield return (feature, scenario);
    }

    private static bool matches(ITestExecutionFilter filter, string uid)
        => filter switch
        {
            TestNodeUidListFilter uids => uids.TestNodeUids.Any(u => u.Value == uid),
            _ => true
        };

    private Task publish(ExecuteRequestContext context, SessionUid sessionUid, TestNode node)
        => context.MessageBus.PublishAsync(this, new TestNodeUpdateMessage(sessionUid, node));

    /// <summary>
    /// Turns runner callbacks into MTP node updates as they happen, so a long suite reports
    /// progressively rather than in one lump at the end.
    /// </summary>
    private sealed class PublishingObserver : IExecutionObserver
    {
        private readonly BobcatTestFramework _framework;
        private readonly ExecuteRequestContext _context;
        private readonly SessionUid _sessionUid;

        public PublishingObserver(BobcatTestFramework framework, ExecuteRequestContext context, SessionUid sessionUid)
        {
            _framework = framework;
            _context = context;
            _sessionUid = sessionUid;
        }

        public void ScenarioStarted(string featureTitle, string scenarioTitle)
        {
            publish(new TestNode
            {
                Uid = SpecNodeMapping.Uid(featureTitle, scenarioTitle),
                DisplayName = SpecNodeMapping.DisplayName(featureTitle, scenarioTitle),
                Properties = new PropertyBag(InProgressTestNodeStateProperty.CachedInstance)
            });
        }

        public void ScenarioCompleted(string featureTitle, ScenarioResult result)
        {
            var properties = new PropertyBag(
                SpecNodeMapping.StateFor(result),
                SpecNodeMapping.TimingFor(result));

            foreach (var trait in SpecNodeMapping.Traits(result.Tags)) properties.Add(trait);
            foreach (var metadata in SpecNodeMapping.OutcomeMetadata(result)) properties.Add(metadata);

            publish(new TestNode
            {
                Uid = SpecNodeMapping.Uid(featureTitle, result.Title),
                DisplayName = SpecNodeMapping.DisplayName(featureTitle, result.Title),
                Properties = properties
            });
        }

        private void publish(TestNode node)
        {
            // The observer contract is synchronous; the message bus is not. Blocking here keeps
            // node updates ordered, which matters because a later state must not overtake an
            // earlier one for the same uid.
            _framework.publish(_context, _sessionUid, node).GetAwaiter().GetResult();
        }

        public void FeatureStarted(string featureTitle) { }
        public void FeatureFinished(string featureTitle) { }
        public void StepStarted(string stepId, StepKind kind, string stepText) { }
        public void StepProgress(string stepId, StepUpdate update) { }
        public void StepFinished(StepResult result) { }
        public void ScenarioFinished(ExecutionResults results) { }
    }
}
