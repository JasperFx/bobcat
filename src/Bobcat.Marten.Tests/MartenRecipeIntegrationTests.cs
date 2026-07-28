using Bobcat.Engine;
using Bobcat.Runtime;
using global::Marten;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.Marten.Tests;

/// <summary>
/// The <c>[MartenEntities]</c> recipe end-to-end against a real Postgres: the generator's
/// envelope, the scenario DI scope, Marten's session, and an actual commit.
/// See <see cref="PostgresFactAttribute"/> for how a missing database is handled.
/// </summary>
public class MartenRecipeIntegrationTests
{
    private const string Schema = "bobcat_recipe";

    [PostgresFact]
    public async Task recipe_persists_documents_and_the_spec_reads_them_back()
    {
        var results = await BuildRunner().RunAll();

        ShouldHaveNoFailures(results);
        results.ExitCode.ShouldBe(0);
    }

    [PostgresFact]
    public async Task documents_constructed_from_columns_really_land_in_the_database()
    {
        // Run only the tagged setup scenario, so nothing resets the data afterwards, then read
        // it back through a brand-new store — proving the commit reached Postgres rather than
        // just the writing session's identity map.
        var results = await BuildRunner().RunAll(tagFilter: "readback");

        ShouldHaveNoFailures(results);

        await using var store = ReadOnlyStore();
        await using var session = store.QuerySession();
        var customers = (await session.Query<Customer>().ToListAsync())
            .OrderBy(c => c.Name)
            .ToList();

        customers.Select(c => c.Name).ShouldBe(["Acme", "Globex"]);

        var acme = customers.Single(c => c.Name == "Acme");
        acme.Region.ShouldBe("West");
        acme.Orders.ShouldBe(3);
        acme.Id.ShouldNotBe(Guid.Empty); // Marten assigned the identity on Store

        customers.Single(c => c.Name == "Globex").Orders.ShouldBe(1);
    }

    [PostgresFact]
    public async Task the_recipe_session_is_the_one_a_FromScopedService_parameter_gets()
    {
        await using var resource = HostResource();
        await resource.Start();
        await resource.BeginScenarioScope();

        var suite = new TestSuite();
        suite.AddResource(resource);
        var context = new SpecExecutionContext("identity", suite: suite);

        var behavior = new MartenStorageBehavior();
        await behavior.Open(context);

        behavior.Session.ShouldBeSameAs(resource.CurrentServices.GetRequiredService<IDocumentSession>());

        await resource.EndScenarioScope();
    }

    private static BobcatRunner BuildRunner()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(Marten_Recipe_Feature.Define());
        runner.Suite.AddResource(HostResource());

        return runner;
    }

    /// <summary>
    /// A host whose container registers Marten, so <c>IDocumentSession</c> is scoped and the
    /// scenario scope owns it. Documents are cleaned between scenarios — persistent state is
    /// reset before the fresh scope opens over it.
    /// </summary>
    private static HostResource HostResource()
        => new(
            () =>
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Services.AddMarten(ConfigureStore);
                return builder.Build();
            },
            reset: async host =>
            {
                var store = host.Services.GetRequiredService<IDocumentStore>();
                await store.Advanced.Clean.DeleteAllDocumentsAsync();
            });

    private static void ConfigureStore(StoreOptions options)
    {
        options.Connection(PostgresEnvironment.ConnectionString);
        options.DatabaseSchemaName = Schema;

        // Host.CreateApplicationBuilder defaults to the Production environment, where AddMarten
        // would otherwise refuse to build schema objects.
        options.AutoCreateSchemaObjects = AutoCreate.All;
    }

    private static IDocumentStore ReadOnlyStore() => DocumentStore.For(ConfigureStore);

    private static void ShouldHaveNoFailures(SuiteResults results)
    {
        var failed = results.Features
            .SelectMany(f => f.Scenarios)
            .SelectMany(s => s.Results.Steps.Select(step => (s.Title, step)))
            .Where(x => x.step.StepStatus is ResultStatus.failed or ResultStatus.error)
            .Select(x => $"{x.Title} / {x.step.StepText}: {Describe(x.step)}")
            .ToList();

        failed.ShouldBeEmpty();
    }

    private static string Describe(StepResult step)
        => step.Exception?.Message
           ?? string.Join("; ", step.Cells
               .Where(c => c.Status != ResultStatus.success && c.Status != ResultStatus.ok)
               .Select(c => c.DisplayText));
}
