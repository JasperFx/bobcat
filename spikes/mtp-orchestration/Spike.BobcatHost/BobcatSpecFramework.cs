using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Requests;
using Microsoft.Testing.Platform.Services;
using Microsoft.Testing.Platform.TestHost;

namespace Spike.BobcatHost;

/// <summary>
/// A stand-in for "Bobcat's Gherkin runner exposed as an MTP test host". It reports fake
/// scenarios rather than executing real ones — the spike is proving the <em>host seam</em>,
/// not re-testing the executor.
/// </summary>
public sealed class BobcatSpecFramework : ITestFramework, IDataProducer
{
    /// <summary>Required by IDataProducer — the framework publishes test node updates.</summary>
    public Type[] DataTypesProduced => [typeof(TestNodeUpdateMessage)];

    private readonly IServiceProvider _serviceProvider;

    public BobcatSpecFramework(ITestFrameworkCapabilities capabilities, IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider;

    public string Uid => nameof(BobcatSpecFramework);
    public string Version => "0.1.0";
    public string DisplayName => "Bobcat Spec Runner (spike)";
    public string Description => "Spike host proving Bobcat can expose itself over Microsoft.Testing.Platform.";

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
                    await Discover(discover, context);
                    break;

                case RunTestExecutionRequest run:
                    await Run(run, context);
                    break;
            }
        }
        finally
        {
            context.Complete();
        }
    }

    private async Task Discover(DiscoverTestExecutionRequest request, ExecuteRequestContext context)
    {
        foreach (var scenario in SpikeScenarios.All)
        {
            await Publish(context, request.Session.SessionUid, scenario,
                new PropertyBag(DiscoveredTestNodeStateProperty.CachedInstance));
        }
    }

    private async Task Run(RunTestExecutionRequest request, ExecuteRequestContext context)
    {
        // The selective-rerun lever: the platform hands the requested subset down as a filter.
        // Whatever the parent asked for over JSON-RPC arrives here.
        Console.Error.WriteLine($"[bobcat-host] filter type = {request.Filter.GetType().Name}");
        if (request.Filter is TestNodeUidListFilter list)
            Console.Error.WriteLine($"[bobcat-host] filter uids = {string.Join(", ", list.TestNodeUids.Select(u => u.Value))}");

        var selected = SpikeScenarios.All.Where(s => Matches(request.Filter, s)).ToList();

        foreach (var scenario in selected)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            await Publish(context, request.Session.SessionUid, scenario,
                new PropertyBag(InProgressTestNodeStateProperty.CachedInstance));

            var started = DateTimeOffset.UtcNow;
            var state = await Execute(scenario, context.CancellationToken);
            var finished = DateTimeOffset.UtcNow;

            await Publish(context, request.Session.SessionUid, scenario, new PropertyBag(
                state,
                new TimingProperty(new TimingInfo(started, finished, finished - started))));
        }
    }

    private static bool Matches(ITestExecutionFilter filter, SpikeScenario scenario)
        => filter switch
        {
            TestNodeUidListFilter uids => uids.TestNodeUids.Any(u => u.Value == scenario.Uid),
            NopFilter => true,
            _ => true
        };

    private static async Task<IProperty> Execute(SpikeScenario scenario, CancellationToken ct)
    {
        switch (scenario.Behavior)
        {
            case Behavior.Pass:
                return PassedTestNodeStateProperty.CachedInstance;

            case Behavior.AssertionFailure:
                // Assertion-shaped: the wire's `failed` state, carrying expected/actual.
                return new FailedTestNodeStateProperty(
                    new InvalidOperationException("Expected 4 but got 5"));

            case Behavior.Exception:
                // Exception-shaped: the wire's `error` state.
                return new ErrorTestNodeStateProperty(
                    new BrokerUnavailableException("rabbit went away"));

            case Behavior.Skip:
                return new SkippedTestNodeStateProperty("deliberately skipped so the spike sees a skip outcome");

            case Behavior.FlakyUntilArmed:
                return Environment.GetEnvironmentVariable("SPIKE_FLAKY_PASSES") == "true"
                    ? PassedTestNodeStateProperty.CachedInstance
                    : new FailedTestNodeStateProperty(
                        new InvalidOperationException("flaky test failing (set SPIKE_FLAKY_PASSES=true to pass)"));

            case Behavior.CrashWhenArmed:
                if (Environment.GetEnvironmentVariable("SPIKE_CRASH") == "true") Environment.Exit(70);
                return PassedTestNodeStateProperty.CachedInstance;

            case Behavior.SlowWhenArmed:
                if (Environment.GetEnvironmentVariable("SPIKE_SLOW") == "true")
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return PassedTestNodeStateProperty.CachedInstance;

            default:
                return PassedTestNodeStateProperty.CachedInstance;
        }
    }

    private async Task Publish(
        ExecuteRequestContext context,
        SessionUid sessionUid,
        SpikeScenario scenario,
        PropertyBag properties)
    {
        // Q4 from the other side: can a Bobcat host *emit* the metadata the policy engine needs?
        foreach (var trait in scenario.Traits)
            properties.Add(new TestMetadataProperty(trait.Key, trait.Value));

        await context.MessageBus.PublishAsync(
            this,
            new TestNodeUpdateMessage(
                sessionUid,
                new TestNode
                {
                    Uid = scenario.Uid,
                    DisplayName = scenario.DisplayName,
                    Properties = properties
                }));
    }
}

public enum Behavior
{
    Pass,
    AssertionFailure,
    Exception,
    Skip,
    FlakyUntilArmed,
    CrashWhenArmed,
    SlowWhenArmed
}

public record SpikeScenario(string Uid, string DisplayName, Behavior Behavior)
{
    public Dictionary<string, string> Traits { get; init; } = new();
}

public static class SpikeScenarios
{
    /// <summary>
    /// Deliberately mirrors the xUnit and tUnit hosts one-for-one so the three can be compared
    /// on identical ground.
    /// </summary>
    public static readonly IReadOnlyList<SpikeScenario> All =
    [
        new("bobcat.spec.passes", "Deposits/a deposit increases the balance", Behavior.Pass),
        new("bobcat.spec.fails_an_assertion", "Deposits/fails_an_assertion", Behavior.AssertionFailure),
        new("bobcat.spec.throws_a_custom_exception", "Deposits/throws_a_custom_exception", Behavior.Exception),
        new("bobcat.spec.is_skipped", "Deposits/is_skipped", Behavior.Skip),
        new("bobcat.spec.carries_traits", "Deposits/carries_traits", Behavior.Pass)
        {
            Traits = new Dictionary<string, string> { ["Isolated"] = "true", ["RecycleOnRetry"] = "rabbit" }
        },
        new("bobcat.spec.flaky_until_told_otherwise", "Deposits/flaky_until_told_otherwise", Behavior.FlakyUntilArmed),
        new("bobcat.spec.kills_the_process_when_armed", "Deposits/kills_the_process_when_armed", Behavior.CrashWhenArmed),
        new("bobcat.spec.sleeps_when_armed", "Deposits/sleeps_when_armed", Behavior.SlowWhenArmed)
    ];
}

public class BrokerUnavailableException(string message) : Exception(message);
