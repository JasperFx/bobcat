using Alba;
using Bobcat;
using Bobcat.Alba;
using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Marten.Tests;
using Bobcat.Runtime;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace Bobcat.CritterStack.Tests;

// --- a saga application: one Wolverine saga, persisted by Marten. -----------------------------

public record DeliveryBooked(Guid DeliverySagaId, string Courier);

public record DeliveryDispatched(Guid DeliverySagaId);

public record DeliveryConfirmed(Guid DeliverySagaId);

public class DeliverySaga : Saga
{
    public Guid Id { get; set; }
    public string Courier { get; set; } = "";
    public string Status { get; set; } = "";

    public static DeliverySaga Start(DeliveryBooked booked)
        => new() { Id = booked.DeliverySagaId, Courier = booked.Courier, Status = "Booked" };

    public void Handle(DeliveryDispatched dispatched) => Status = "Dispatched";

    public void Handle(DeliveryConfirmed confirmed) => MarkCompleted();
}

/// <summary>A saga no handler is discovered for, so no storage owns it.</summary>
public class ParkedSaga : Saga
{
    public Guid Id { get; set; }
}

/// <summary>
/// The saga lane composed onto the messaging vocabulary: <see cref="CritterStackFixture"/> carries
/// <c>When {command} is received</c>, which is how a saga starts; <see cref="SagaGrammars"/>
/// asserts what it did.
/// </summary>
[FixtureTitle("Deliveries")]
[IncludeGrammars(typeof(SagaGrammars))]
public class DeliveriesFixture : CritterStackFixture;

/// <summary>
/// Issue #281 end to end: <c>Deliveries.feature</c>, written only in shipped vocabulary, against a
/// Wolverine saga persisted by Marten — plus the two ways a saga step must refuse to pass quietly.
/// </summary>
public class SagaGrammarSpecTests
{
    private const string schema = "bobcat_deliveries";

    [PostgresFact]
    public async Task the_saga_grammar_feature_compiles_runs_and_renders()
    {
        await cleanSchema();

        await using var resource = hostResource();
        await resource.Start();

        var suite = new TestSuite();
        suite.AddResource(resource);

        var feature = Deliveries_Feature.Define();
        feature.Scenarios.Count.ShouldBe(3);

        foreach (var scenario in feature.Scenarios)
        {
            var results = await run(feature, scenario, suite);

            foreach (var step in results.Steps)
            {
                step.StepStatus.ShouldBeOneOf([ResultStatus.success, ResultStatus.ok],
                    $"{scenario.Title} / {step.StepText}: {step.DescribeFailure()}");
            }

            SpecRender.FromResults(scenario.Title, results, feature.Title).Steps.ShouldNotBeEmpty();
        }
    }

    [PostgresFact]
    public async Task a_saga_type_no_storage_owns_is_refused_rather_than_read_as_absent()
    {
        await cleanSchema();
        await using var resource = hostResource();
        await resource.Start();

        await inScenario(resource, async grammar =>
        {
            // Without the guard this would pass: the storage view answers null for an unknown type
            // exactly as it does for a missing saga.
            var ex = await Should.ThrowAsync<SpecCriticalException>(() =>
                grammar.ThenNoSagaExistsWithId(typeof(ParkedSaga), Guid.NewGuid().ToString()));

            ex.Message.ShouldContain(typeof(ParkedSaga).FullName!);
            ex.Message.ShouldContain(typeof(DeliverySaga).FullName!);
        });
    }

    [PostgresFact]
    public async Task an_active_saga_that_does_not_match_fails_naming_only_the_columns_the_row_named()
    {
        await cleanSchema();
        await using var resource = hostResource();
        await resource.Start();

        var id = Guid.NewGuid();
        using (var scope = resource.RootServices.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new DeliveryBooked(id, "acme"));
        }

        await inScenario(resource, async grammar =>
        {
            var ex = await Should.ThrowAsync<SpecAssertionException>(() =>
                grammar.ThenTheSagaWithIdIsActive(typeof(DeliverySaga), id.ToString(),
                    new StepTable(["Status"], [["Dispatched"]])));

            ex.Message.ShouldContain("Status: expected Dispatched, was Booked");
            ex.Message.ShouldNotContain("Courier");
        });
    }

    [PostgresFact]
    public async Task a_missing_saga_is_named_rather_than_passing_as_active()
    {
        await cleanSchema();
        await using var resource = hostResource();
        await resource.Start();

        await inScenario(resource, async grammar =>
        {
            var id = Guid.NewGuid().ToString();

            var ex = await Should.ThrowAsync<SpecAssertionException>(() =>
                grammar.ThenTheSagaWithIdIsActive(typeof(DeliverySaga), id, null));
            ex.Message.ShouldContain("none is stored");

            // …and the absence assertion is happy with exactly that state.
            await Should.NotThrowAsync(() => grammar.ThenNoSagaExistsWithId(typeof(DeliverySaga), id));
        });
    }

    private static async Task inScenario(AlbaResource resource, Func<SagaGrammars, Task> body)
    {
        var suite = new TestSuite();
        suite.AddResource(resource);

        var grammar = new SagaGrammars
        {
            Context = new SpecExecutionContext("saga", suite: suite) { Cancellation = CancellationToken.None }
        };

        await resource.BeginScenarioScope();
        try
        {
            await body(grammar);
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
            }).IntegrateWithWolverine();

            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<DeliverySaga>();
            });

            return await AlbaHost.For(builder, _ => { });
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
