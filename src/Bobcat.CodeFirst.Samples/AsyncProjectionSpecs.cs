using Bobcat.CritterStack;
using Bobcat.Runtime;
using Marten;
using Marten.Metadata;

namespace Bobcat.CodeFirst.Samples;

/// <summary>
/// Port of Marten's <c>DaemonTests/Aggregations/build_aggregate_projection.cs</c>
/// <c>simple_scenario</c>: streams appended for two tenants on a conjoined-tenancy store, an async
/// snapshot projection built by the daemon, and the per-tenant read models checked once the daemon
/// has caught up. The projection wait is the step that makes this an integration test rather than a
/// race.
/// </summary>
[FixtureTitle("Async projections across tenants")]
public class AsyncProjectionSpecs : Specification
{
    private static readonly TimeSpan daemonTimeout = TimeSpan.FromSeconds(20);

    // build_aggregate_projection.cs:30 simple_scenario
    [Scenario("A snapshot is built per tenant by the async daemon")]
    public void simple_scenario()
    {
        var seeds = new[]
        {
            new StreamSeed("blue", "one", new MTAEvent(), new MTBEvent()),
            new StreamSeed("blue", "two", new MTBEvent(), new MTBEvent()),
            new StreamSeed("blue", "three", new MTAEvent(), new MTAEvent()),
            new StreamSeed("red", "one", new MTAEvent(), new MTBEvent(), new MTAEvent()),
            new StreamSeed("red", "two", new MTBEvent(), new MTBEvent(), new MTBEvent()),
            new StreamSeed("red", "five", new MTBEvent(), new MTBEvent(), new MTBEvent())
        };

        Given("these streams are started, per tenant", async ctx =>
            {
                var store = ctx.GetRootService<IDocumentStore>(Hosts.Projections);
                await using var session = store.LightweightSession();
                foreach (var seed in seeds)
                    session.ForTenant(seed.Tenant).Events.StartStream<SimpleEntity>(seed.Stream, seed.Events);
                await session.SaveChangesAsync(ctx.Cancellation);
            })
            .WithRows(seeds);

        When("the async daemon has caught up with the high-water mark",
            ctx => ctx.WaitForNonStaleProjectionsAsync(daemonTimeout, hostResource: Hosts.Projections));

        ThenRows("the blue tenant's SimpleEntity read models", ctx => entitiesFor(ctx, "blue"))
            .KeyedBy("Id")
            .ShouldMatch(
                new { Id = "one", A = 1, B = 1 },
                new { Id = "two", A = 0, B = 2 },
                new { Id = "three", A = 2, B = 0 });

        ThenRows("the red tenant's SimpleEntity read models", ctx => entitiesFor(ctx, "red"))
            .KeyedBy("Id")
            .ShouldMatch(
                new { Id = "one", A = 2, B = 1 },
                new { Id = "two", A = 0, B = 3 },
                new { Id = "five", A = 0, B = 3 });
    }

    private static async Task<IReadOnlyList<SimpleEntity>> entitiesFor(Engine.IStepContext ctx, string tenant)
    {
        await using var session = ctx.GetRootService<IDocumentStore>(Hosts.Projections).QuerySession(tenant);
        return await session.Query<SimpleEntity>().ToListAsync(ctx.Cancellation);
    }
}

/// <summary>One row of the Given table: a tenant, a stream key, and the events that start it.</summary>
public record StreamSeed(string Tenant, string Stream, params object[] Events);

// --- the domain, from DaemonTests/MultiTenancy and build_aggregate_projection.cs -------------------

public record MTAEvent;
public record MTBEvent;

public class SimpleEntity : ITenanted
{
    public string Id { get; set; } = "";
    public int A { get; set; }
    public int B { get; set; }
    public string? TenantId { get; set; }

    public void Apply(MTAEvent _) => A++;
    public void Apply(MTBEvent _) => B++;
}
