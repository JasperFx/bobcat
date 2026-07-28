using Bobcat.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.EntityFrameworkCore.Tests;

public class EfRecipeTests
{
    private static BobcatRunner BuildRunner(string databaseName)
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(Ef_Recipe_Feature.Define());
        runner.Suite.AddResource(new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddDbContext<ShopContext>(o => o.UseInMemoryDatabase(databaseName));
            return builder.Build();
        }));

        return runner;
    }

    [Fact]
    public async Task recipe_with_no_Row_constructs_entities_from_columns_and_persists_them()
    {
        var results = await BuildRunner("no-row").RunAll();

        results.ExitCode.ShouldBe(0);

        await using var context = NewContext("no-row");
        var customers = context.Customers
            .Where(c => c.Region != "Premium" && c.Region != "Audit")
            .OrderBy(c => c.Name)
            .ToList();

        // The grammar has no Row body at all — these came straight from the columns.
        customers.Select(c => c.Name).ShouldBe(["Acme", "Globex"]);
        customers.Single(c => c.Name == "Acme").Region.ShouldBe("West");
        customers.Single(c => c.Name == "Acme").Orders.ShouldBe(3);
        customers.Single(c => c.Name == "Globex").Orders.ShouldBe(1);
    }

    [Fact]
    public async Task a_Row_override_customizes_construction()
    {
        var results = await BuildRunner("row-override").RunAll();

        results.ExitCode.ShouldBe(0);

        await using var context = NewContext("row-override");
        var premium = context.Customers.Single(c => c.Region == "Premium");

        premium.Name.ShouldBe("Initech");
        premium.Orders.ShouldBe(20); // the Row body multiplied by 10

        // The Row body added this through its own [FromScopedService] ShopContext. It is here
        // only because the recipe's SaveChangesAsync ran on that same scoped instance.
        context.Customers.Single(c => c.Region == "Audit").Name.ShouldBe("Initech-audit");
    }

    [Fact]
    public async Task the_recipe_context_is_the_scenario_scoped_instance()
    {
        // Resolve the context the same way a [FromScopedService] parameter would, and check the
        // recipe's behavior lands on that exact instance — that identity is what batches the save.
        await using var resource = new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddDbContext<ShopContext>(o => o.UseInMemoryDatabase("identity"));
            return builder.Build();
        });

        await resource.Start();
        await resource.BeginScenarioScope();

        var suite = new TestSuite();
        suite.AddResource(resource);
        var context = new Bobcat.Engine.SpecExecutionContext("identity", suite: suite);

        var behavior = new EfCoreStorageBehavior(typeof(ShopContext));
        await behavior.Open(context);

        behavior.Context.ShouldBeSameAs(resource.CurrentServices.GetRequiredService<ShopContext>());

        await behavior.Close();
        await resource.EndScenarioScope();
    }

    [Fact]
    public async Task rows_are_added_to_the_context_but_only_saved_when_the_envelope_closes()
    {
        await using var resource = new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddDbContext<ShopContext>(o => o.UseInMemoryDatabase("batching"));
            return builder.Build();
        });

        await resource.Start();
        await resource.BeginScenarioScope();

        var suite = new TestSuite();
        suite.AddResource(resource);
        var stepContext = new Bobcat.Engine.SpecExecutionContext("batching", suite: suite);

        var behavior = new EfCoreStorageBehavior(typeof(ShopContext));
        await behavior.Open(stepContext);
        await behavior.Row(new Customer("A", "West", 1));
        await behavior.Row(new Customer("B", "East", 2));

        // Still pending — nothing has been written yet.
        NewContext("batching").Customers.Count().ShouldBe(0);

        await behavior.Close();

        NewContext("batching").Customers.Count().ShouldBe(2);

        await resource.EndScenarioScope();
    }

    private static ShopContext NewContext(string databaseName)
        => new(new DbContextOptionsBuilder<ShopContext>().UseInMemoryDatabase(databaseName).Options);
}
