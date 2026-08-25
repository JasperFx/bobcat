using Bobcat;
using Bobcat.Engine;
using Bobcat.Marten.Tests;
using Bobcat.Rendering;
using Bobcat.Runtime;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// The #104 acceptance proof: <c>Wallet.feature</c> is written ONLY in shipped Critter Stack grammar
/// steps (no fixture-specific steps exist), it compiles through the generator against a real domain,
/// runs on Marten over the repo's Postgres, and renders. The grammar rides the base-class route —
/// <see cref="WalletFixture"/> derives from <see cref="CritterStackFixture"/> and declares nothing.
/// </summary>
/// <remarks>
/// Fisher coverage is not possible in this repo yet: every published Fisher requires
/// JasperFx.Events ≥ 2.47.0, above the repo's 2.37.0 pin — that repo-wide alignment bump is issue
/// #125, in flight on another branch. When it lands, the same feature runs against a Fisher host by
/// swapping AddMarten for AddFisher, because the fixture binds to JasperFx.Events, not to Marten.
/// </remarks>
public class GrammarSpecTests
{
    private const string schema = "bobcat_wallet_grammar";

    [PostgresFact]
    public async Task the_shipped_grammar_feature_compiles_runs_on_marten_and_renders()
    {
        await cleanSchema();

        await using var resource = hostResource();
        await resource.Start();

        var suite = new TestSuite();
        suite.AddResource(resource);

        var feature = Wallet_Feature.Define();

        // The vocabulary is entirely the shipped grammar, discovered from the referenced assembly.
        feature.Domain.ShouldBe("Wallets");
        feature.TriggeredBy.ShouldBe("the wallet holder");
        feature.Scenarios.Count.ShouldBe(4);

        foreach (var scenario in feature.Scenarios)
        {
            var results = await run(feature, scenario, suite);

            // Every step passed — and it rendered to the SpecRender model both console and HTML consume.
            var render = SpecRender.FromResults(scenario.Title, results, feature.Title);
            render.Steps.ShouldNotBeEmpty();

            foreach (var step in results.Steps)
            {
                step.StepStatus.ShouldBeOneOf(ResultStatus.success, ResultStatus.ok);
            }

            // The run evidence (issue #107): the typed steps record what the scenario observably
            // touched — the aggregate arranged, the commands actually dispatched, the events the
            // stream actually gained, the message the tracked session actually sent, the read
            // model actually loaded — in first-touch order, deduplicated.
            if (scenario.Title == "Crediting a wallet emits the credited event and sends a notification")
            {
                results.TouchedTypes.Select(t => t.Name).ShouldBe(
                    ["Wallet", "OpenWallet", "WalletOpened", "CreditWallet", "WalletCredited",
                     "WalletCreditedNotification", "WalletSummary"]);
            }

            // The sad path proves evidence is observed, never asserted: the rejected command is
            // recorded (the spec touched it), but no event type is — none was emitted.
            if (scenario.Title == "Crediting a non-positive amount fails and emits nothing")
            {
                results.TouchedTypes.Select(t => t.Name).ShouldBe(
                    ["Wallet", "OpenWallet", "WalletOpened", "CreditWallet"]);
            }
        }
    }

    private static async Task<ExecutionResults> run(FeatureDefinition feature, ScenarioDefinition scenario, TestSuite suite)
    {
        var fixture = (Fixture)Activator.CreateInstance(feature.FixtureType)!;

        var plan = new ExecutionPlan(scenario.Title, TimeSpan.FromSeconds(60));
        scenario.BuildPlan(fixture, plan);

        var context = new SpecExecutionContext(scenario.Title, suite: suite) { Cancellation = CancellationToken.None };
        fixture.Context = context;

        var resource = suite.GetResource<IHostResource>();
        await resource.BeginScenarioScope();
        try
        {
            if (feature.BeforeEach != null) await feature.BeforeEach(fixture, context);
            var executor = new Executor([new FailureLevelContinuationRule()]);
            await executor.Execute(plan, context);
        }
        finally
        {
            if (feature.AfterEach != null) await feature.AfterEach(fixture, context);
            await resource.EndScenarioScope();
        }

        return context.Results;
    }

    // Wolverine dispatching into Marten with Marten's own async daemon projecting WalletSummary —
    // the same shape as MartenIntegrationTests, and again with no WolverineFx.Marten integration.
    private static HostResource hostResource()
        => new(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddMarten(configureStore).AddAsyncDaemon(DaemonMode.Solo);
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<WalletHandler>();
            });
            return builder.Build();
        });

    private static void configureStore(StoreOptions options)
    {
        options.Connection(PostgresEnvironment.ConnectionString);
        options.DatabaseSchemaName = schema;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Projections.Snapshot<WalletSummary>(SnapshotLifecycle.Async);
    }

    private static async Task cleanSchema()
    {
        await using var store = DocumentStore.For(configureStore);
        await store.Advanced.Clean.DeleteAllDocumentsAsync();
        await store.Advanced.Clean.DeleteAllEventDataAsync();
    }
}
