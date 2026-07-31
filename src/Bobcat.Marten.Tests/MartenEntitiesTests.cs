using Bobcat.Engine;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;

namespace Bobcat.Marten.Tests;

/// <summary>
/// The Marten recipe's contract, exercised against a substituted <see cref="IDocumentSession"/>
/// so no Postgres is needed — the point under test is the envelope's behavior (resolve from the
/// scenario scope, Store per row, one SaveChangesAsync at close), not Marten's own persistence.
/// The end-to-end "recipe with no Row body" path is covered against a real store in
/// Bobcat.EntityFrameworkCore.Tests, which shares the generated envelope.
/// </summary>
public class MartenEntitiesTests
{
    [Fact]
    public void the_attribute_is_a_grammar_behavior_the_generator_can_recognize()
    {
        new MartenEntitiesAttribute().ShouldBeAssignableTo<GrammarBehaviorAttribute>();
        new MartenEntitiesAttribute<Customer>().EntityType.ShouldBe(typeof(Customer));

        // The non-generic form leaves the entity to Row's return type.
        new MartenEntitiesAttribute().EntityType.ShouldBeNull();
    }

    [Fact]
    public void Build_produces_a_marten_storage_behavior()
    {
        new MartenEntitiesAttribute().Build().ShouldBeOfType<MartenStorageBehavior>();
        new MartenEntitiesAttribute<Customer>().Build().ShouldBeOfType<MartenStorageBehavior>();
    }

    [Fact]
    public async Task resolves_the_session_from_the_scenario_scope_not_the_root()
    {
        var session = Substitute.For<IDocumentSession>();
        await using var resource = hostWith(session);
        await resource.Start();
        await resource.BeginScenarioScope();

        var behavior = new MartenStorageBehavior();
        await behavior.Open(stepContextFor(resource));

        // The same instance a step would get from [FromScopedService] IDocumentSession.
        behavior.Session.ShouldBeSameAs(resource.CurrentServices.GetRequiredService<IDocumentSession>());

        await resource.EndScenarioScope();
    }

    [Fact]
    public async Task stores_each_row_and_saves_exactly_once_when_the_envelope_closes()
    {
        var session = Substitute.For<IDocumentSession>();
        await using var resource = hostWith(session);
        await resource.Start();
        await resource.BeginScenarioScope();

        var behavior = new MartenStorageBehavior();
        await behavior.Open(stepContextFor(resource));

        var first = new Customer("Acme", "West", 3);
        var second = new Customer("Globex", "East", 1);
        await behavior.Row(first);
        await behavior.Row(second);

        // Nothing is flushed while rows are still arriving.
        await session.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        await behavior.Close();

        // StoreObjects keeps each product under its own document type rather than 'object'.
        session.Received(1).StoreObjects(Arg.Is<IEnumerable<object>>(d => ReferenceEquals(d.Single(), first)));
        session.Received(1).StoreObjects(Arg.Is<IEnumerable<object>>(d => ReferenceEquals(d.Single(), second)));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        await resource.EndScenarioScope();
    }

    [Fact]
    public async Task a_null_row_product_is_a_configuration_error()
    {
        var session = Substitute.For<IDocumentSession>();
        await using var resource = hostWith(session);
        await resource.Start();
        await resource.BeginScenarioScope();

        var behavior = new MartenStorageBehavior();
        await behavior.Open(stepContextFor(resource));

        var ex = await Should.ThrowAsync<BobcatConfigurationException>(async () => await behavior.Row(null));
        ex.Message.ShouldContain("must return the entity to persist");

        await resource.EndScenarioScope();
    }

    [Fact]
    public async Task disposing_the_behavior_leaves_the_scenario_scoped_session_alive()
    {
        var session = Substitute.For<IDocumentSession>();
        await using var resource = hostWith(session);
        await resource.Start();
        await resource.BeginScenarioScope();

        var behavior = new MartenStorageBehavior();
        await behavior.Open(stepContextFor(resource));
        await behavior.DisposeAsync();

        // The scope owns the session; the behavior must not dispose it out from under the steps.
        await session.DidNotReceive().DisposeAsync();

        await resource.EndScenarioScope();
    }

    private static HostResource hostWith(IDocumentSession session)
        => new(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddScoped(_ => session);
            return builder.Build();
        });

    private static IStepContext stepContextFor(ITestResource resource)
    {
        var suite = new TestSuite();
        suite.AddResource(resource);
        return new SpecExecutionContext("recipe", suite: suite);
    }
}
