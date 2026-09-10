using Alba;
using Bobcat;
using Bobcat.Alba;
using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Marten.Tests;
using Bobcat.Runtime;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Wolverine;

namespace Bobcat.CritterStack.Tests;

// --- a document application. No projection, no stream, no event anywhere. -------------------

public class Shipment
{
    public Guid Id { get; set; }
    public string Origin { get; set; } = "";
    public string Destination { get; set; } = "";
    public string? Carrier { get; set; }
    public decimal WeightKg { get; set; }
    public string Status { get; set; } = "";
}

public record ShipmentRequest(string Origin, string Destination, string Carrier, decimal WeightKg);

public record CancelShipmentRequest(Guid ShipmentId);

public record ShipmentBooked(string Origin, string Destination, string Carrier, decimal WeightKg);

public class ShipmentHandler
{
    public static void Handle(ShipmentBooked command) { }
}

/// <summary>
/// The whole composition, and the answer to issue #270: documents from
/// <see cref="DocumentGrammars"/>, HTTP from <see cref="HttpGrammars"/>, messaging from
/// <see cref="CritterStackFixture"/> — against an application with no event store at all.
/// </summary>
/// <remarks>
/// The base class is here for the messaging and refusal vocabulary, not for event sourcing. Its
/// stream steps are simply never used by this feature, and the ones that are — <c>Then {message}
/// is sent</c> and the response assertions — work fine without a stream, which
/// <c>HttpActComposesWithMessageAssertionsTests</c> pins separately.
/// <para>
/// <see cref="DocumentGrammars"/> itself needs none of that: it derives from <see cref="Fixture"/>
/// and is exercised standalone by the failure tests below, so a project that only wants documents
/// composes it onto a bare fixture.
/// </para>
/// </remarks>
[FixtureTitle("Shipments")]
[IncludeGrammars(typeof(DocumentGrammars))]
[IncludeGrammars(typeof(HttpGrammars), "", null, null, 60000)]
public class ShipmentsFixture : CritterStackFixture;

/// <summary>
/// Issue #270 end to end: <c>Shipments.feature</c>, written only in shipped vocabulary, against a
/// document-backed Wolverine application with no event sourcing at all.
/// </summary>
public class DocumentGrammarSpecTests
{
    private const string schema = "bobcat_shipments";

    [PostgresFact]
    public async Task the_document_grammar_feature_compiles_runs_and_renders_with_no_event_store()
    {
        await cleanSchema();

        await using var resource = hostResource();
        await resource.Start();

        var suite = new TestSuite();
        suite.AddResource(resource);

        var feature = Shipments_Feature.Define();
        feature.Scenarios.Count.ShouldBe(3);

        foreach (var scenario in feature.Scenarios)
        {
            var results = await run(feature, scenario, suite);

            foreach (var step in results.Steps)
            {
                step.StepStatus.ShouldBeOneOf(ResultStatus.success, ResultStatus.ok);
            }

            SpecRender.FromResults(scenario.Title, results, feature.Title).Steps.ShouldNotBeEmpty();
        }
    }

    [PostgresFact]
    public async Task a_document_that_does_not_match_fails_naming_only_the_columns_the_row_named()
    {
        await cleanSchema();

        await using var resource = hostResource();
        await resource.Start();
        var suite = new TestSuite();
        suite.AddResource(resource);

        var grammar = new DocumentGrammars();
        var context = new SpecExecutionContext("mismatch", suite: suite) { Cancellation = CancellationToken.None };
        grammar.Context = context;

        await resource.BeginScenarioScope();
        try
        {
            var id = Guid.NewGuid();
            await grammar.GivenDocumentsOfType(typeof(Shipment), new StepTable(
                ["Id", "Origin", "Destination", "Status"],
                [[id.ToString(), "Dallas", "Austin", "Booked"]]));

            var ex = await Should.ThrowAsync<SpecAssertionException>(() =>
                grammar.ThenTheDocumentWithIdHas(typeof(Shipment), id.ToString(),
                    new StepTable(["Status"], [["Cancelled"]])));

            ex.Message.ShouldContain("Status: expected Cancelled, was Booked");
            // Columns the row did not name are not part of the scenario and are never reported.
            ex.Message.ShouldNotContain("WeightKg");
            ex.Message.ShouldNotContain("Carrier");
        }
        finally
        {
            await resource.EndScenarioScope();
        }
    }

    [PostgresFact]
    public async Task a_missing_document_is_named_rather_than_passing_vacuously()
    {
        await cleanSchema();

        await using var resource = hostResource();
        await resource.Start();
        var suite = new TestSuite();
        suite.AddResource(resource);

        var grammar = new DocumentGrammars();
        var context = new SpecExecutionContext("missing", suite: suite) { Cancellation = CancellationToken.None };
        grammar.Context = context;

        await resource.BeginScenarioScope();
        try
        {
            var id = Guid.NewGuid();

            var ex = await Should.ThrowAsync<SpecAssertionException>(() =>
                grammar.ThenTheDocumentWithIdHas(typeof(Shipment), id.ToString(),
                    new StepTable(["Status"], [["Booked"]])));

            ex.Message.ShouldContain("but none exists");

            // …and the absence assertion is happy with exactly that state.
            await Should.NotThrowAsync(() =>
                grammar.ThenNoDocumentExistsWithId(typeof(Shipment), id.ToString()));
        }
        finally
        {
            await resource.EndScenarioScope();
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

    private static AlbaResource hostResource()
        => new(async () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddMarten(opts =>
            {
                opts.Connection(PostgresEnvironment.ConnectionString);
                opts.DatabaseSchemaName = schema;
                // Deliberately no projections, no async daemon: this is a document store.
            });
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ShipmentHandler>();
            });

            return await AlbaHost.For(builder, app =>
            {
                app.MapPost("/shipments", async (ShipmentRequest request, IMessageBus bus, IDocumentSession session) =>
                {
                    session.Store(new Shipment
                    {
                        Id = Guid.NewGuid(),
                        Origin = request.Origin,
                        Destination = request.Destination,
                        Carrier = request.Carrier,
                        WeightKg = request.WeightKg,
                        Status = "Booked",
                    });
                    await session.SaveChangesAsync();

                    await bus.PublishAsync(new ShipmentBooked(
                        request.Origin, request.Destination, request.Carrier, request.WeightKg));

                    return Results.Accepted();
                });

                app.MapPost("/shipments/cancel", async (CancelShipmentRequest command, IDocumentSession session) =>
                {
                    session.Delete<Shipment>(command.ShipmentId);
                    await session.SaveChangesAsync();
                    return Results.Ok();
                });
            });
        });

    private static async Task cleanSchema()
    {
        await using var store = DocumentStore.For(opts =>
        {
            opts.Connection(PostgresEnvironment.ConnectionString);
            opts.DatabaseSchemaName = schema;
        });
        await store.Advanced.Clean.CompletelyRemoveAllAsync();
    }
}
