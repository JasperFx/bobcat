using Bobcat;
using Bobcat.Alba;
using Bobcat.Engine;
using Bobcat.Marten.Tests;
using Bobcat.Rendering;
using Bobcat.Runtime;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Wolverine;
using Alba;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// The spec fixture for <c>WalletHttp.feature</c> — issue #212's success measure in one line:
/// the store vocabulary from <see cref="CritterStackHttpFixture"/> (whose own base is
/// <see cref="CritterStackFixture"/>), the HTTP lane from the base-declared
/// <see cref="HttpGrammars"/> module, re-parameterized here with the route prefix (the
/// most-derived [IncludeGrammars] wins). No hand-written steps, no third monolith.
/// </summary>
[FixtureTitle("Wallet over HTTP")]
// The timeout is raised for the same reason as HttpActComposesWithMessageAssertionsTests:
// the tracked window here also covers Wolverine's first-message dynamic codegen, which a real
// spec suite pays once but a per-test host pays every time. 5s is the product default and is
// fine for a warm host; on a loaded CI runner it is not. See the issue on the default itself.
[IncludeGrammars(typeof(HttpGrammars), "/wallets", null, null, 60000)]
public class WalletOverHttpFixture : CritterStackHttpFixture;

/// <summary>
/// Issue #210 end to end: <c>WalletHttp.feature</c>, written only in shipped vocabulary, drives
/// a real collapsed HTTP endpoint (the endpoint IS the handler — appends in one transaction,
/// publishes the notification) over Alba's TestServer, through the generator, on Marten over the
/// repo's Postgres — happy path and the 400 refusal — with the async daemon's read model wait
/// composing with the tracked HTTP act.
/// </summary>
public class HttpGrammarSpecTests
{
    private const string schema = "bobcat_wallet_http";

    [PostgresFact]
    public async Task the_http_grammar_feature_compiles_runs_on_marten_and_renders()
    {
        await cleanSchema();

        await using var resource = hostResource();
        await resource.Start();

        var suite = new TestSuite();
        suite.AddResource(resource);

        var feature = Wallet_over_HTTP_Feature.Define();

        feature.Domain.ShouldBe("Wallets");
        feature.TriggeredBy.ShouldBe("the wallet holder");
        feature.Scenarios.Count.ShouldBe(2);

        foreach (var scenario in feature.Scenarios)
        {
            var results = await run(feature, scenario, suite);

            var render = SpecRender.FromResults(scenario.Title, results, feature.Title);
            render.Steps.ShouldNotBeEmpty();

            foreach (var step in results.Steps)
            {
                step.StepStatus.ShouldBeOneOf(ResultStatus.success, ResultStatus.ok);
            }

            // Run evidence (issue #107) flows from the composed HTTP act exactly as from a
            // command act: the aggregate arranged, the command actually posted, the event the
            // stream actually gained, the message the tracked session actually saw, the read
            // model actually loaded.
            if (scenario.Title == "Crediting a wallet over HTTP emits the credited event")
            {
                results.TouchedTypes.Select(t => t.Name).ShouldBe(
                    ["Wallet", "WalletOpened", "CreditWallet", "WalletCredited",
                     "WalletCreditedNotification", "WalletSummary"]);
            }

            // The refusal: the command was posted (touched), nothing was appended.
            if (scenario.Title == "A refused credit returns 400 and appends nothing")
            {
                results.TouchedTypes.Select(t => t.Name).ShouldBe(
                    ["Wallet", "WalletOpened", "CreditWallet"]);
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

    /// <summary>
    /// The collapsed endpoint shape issue #210 declares the default: the endpoint IS the handler,
    /// appending to the stream in one transaction and returning an honest status — a guard
    /// refusing with ProblemDetails/400. The notification still goes through Wolverine so the
    /// tracked session (and <c>Then WalletCreditedNotification is sent</c>) sees it.
    /// </summary>
    private static AlbaResource hostResource()
        => new(async () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddMarten(configureStore).AddAsyncDaemon(DaemonMode.Solo);
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<WalletHandler>();
            });
            return await AlbaHost.For(builder, app =>
            {
                app.MapPost("/wallets/credit", async (CreditWallet command, IDocumentStore store, IMessageBus bus) =>
                {
                    if (command.Amount <= 0)
                        return Results.Problem(detail: "Credit amount must be positive", statusCode: 400);

                    await using var session = store.LightweightSession();
                    session.Events.Append(command.WalletId, new WalletCredited(command.WalletId, command.Amount));
                    await session.SaveChangesAsync();

                    await bus.PublishAsync(new WalletCreditedNotification(command.WalletId));
                    return Results.Ok();
                });
            });
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
