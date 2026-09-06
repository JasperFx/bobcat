using Alba;
using Bobcat.Alba;
using Bobcat.Engine;
using Bobcat.Marten.Tests;
using Bobcat.Runtime;
using Bobcat.Wolverine;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace Bobcat.CritterStack.Tests;

// --- the wait itself, proven deterministically (no database, always runs) ----------------------

public record GatedWork;

/// <summary>
/// A handler the test can hold at the door: it announces it started, waits for the test's
/// release, then announces it finished. That makes "the HTTP response came back while the work
/// the call caused was still in flight" a fact the test controls, not a race it hopes to win.
/// </summary>
public class GatedWorkHandler
{
    public static TaskCompletionSource Entered = null!;
    public static TaskCompletionSource Release = null!;
    public static TaskCompletionSource Completed = null!;

    public static void Reset()
    {
        Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static async Task Handle(GatedWork _)
    {
        Entered.TrySetResult();
        await Release.Task;
        Completed.TrySetResult();
    }
}

/// <summary>
/// The scenario issue #211 opens with: an HTTP endpoint that <i>publishes</i> its work to a local
/// queue, so a bare Alba call returns when the response does while the interesting effects are
/// still in flight. The gated handler turns the ordering into something asserted, not raced.
/// </summary>
public class TrackedHttpCallTests
{
    private static AlbaResource gatedHostResource()
        => new(async () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<GatedWorkHandler>();
            });
            return await AlbaHost.For(builder, app =>
            {
                app.MapPost("/work", async (IMessageBus bus) =>
                {
                    await bus.PublishAsync(new GatedWork());
                    return Results.Accepted();
                });
            });
        });

    [Fact]
    public async Task a_bare_alba_call_returns_before_the_work_it_caused_lands()
    {
        GatedWorkHandler.Reset();
        await using var resource = gatedHostResource();
        await resource.Start();

        await resource.AlbaHost.Scenario(s =>
        {
            s.Post.Url("/work");
            s.StatusCodeShouldBe(202);
        });

        // The response is back; the published message has not been handled. This is the gap the
        // tracked call exists to close — a Then step running here would assert against nothing.
        GatedWorkHandler.Completed.Task.IsCompleted.ShouldBeFalse();

        // Drain before disposing the host.
        GatedWorkHandler.Release.TrySetResult();
        await GatedWorkHandler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task the_tracked_call_returns_only_after_the_caused_work_lands()
    {
        GatedWorkHandler.Reset();
        await using var resource = gatedHostResource();
        await resource.Start();
        var context = contextFor(resource);

        // The IStepContext surface for a hand-written fixture: the same Alba scenario, executed
        // inside Wolverine's tracked session.
        var tracked = context.ExecuteAndWaitAsync(
            () => resource.AlbaHost.Scenario(s =>
            {
                s.Post.Url("/work");
                s.StatusCodeShouldBe(202);
            }),
            timeoutInMilliseconds: 30_000);

        // The handler is running, so the HTTP response has already completed inside the delegate…
        await GatedWorkHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // …and the tracked call is still waiting on the work the call caused.
        tracked.IsCompleted.ShouldBeFalse();

        GatedWorkHandler.Release.TrySetResult();
        var session = await tracked;

        GatedWorkHandler.Completed.Task.IsCompleted.ShouldBeTrue();
        session.Executed.SingleMessage<GatedWork>().ShouldNotBeNull();
    }

    private static IStepContext contextFor(IHostResource resource)
    {
        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>(null).Returns(resource);
        context.Cancellation.Returns(CancellationToken.None);
        return context;
    }
}

// --- the fixture-level capture against a real store (Marten on the repo's Postgres) ------------

/// <summary>
/// <see cref="CritterStackFixture.WhenTracked{T}(Func{Task{T}}, int, Func{TrackedSessionConfiguration, TrackedSessionConfiguration}?)"/>
/// end to end: an Alba HTTP call whose endpoint publishes a command to a local queue, whose handler
/// appends to the event store, whose async daemon projects a read model — and the whole existing
/// assertion vocabulary (<c>ThenEvents</c>, <c>Then {event} is emitted</c>, <c>ThenMessagesSent</c>,
/// <c>ThenDocument</c> with its projection wait) reading the tracked capture unchanged.
/// </summary>
public class TrackedHttpFixtureTests
{
    private const string schema = "bobcat_trackedhttp";

    private class HttpActFixture : CritterStackFixture;

    [PostgresFact]
    public async Task the_tracked_http_call_feeds_the_same_assertion_vocabulary_as_a_command()
    {
        await cleanSchema();
        await using var resource = hostResource();
        await resource.Start();
        var fixture = fixtureFor(resource);
        var id = Guid.NewGuid();

        await fixture.GivenEvents<Account>(id, new AccountOpened(id, "Ann"));

        // The act: HTTP in, tracked. The endpoint returns 202 the moment the command is on the
        // local queue; WhenTracked returns once the handler has actually appended.
        var result = await fixture.WhenTracked(
            () => resource.AlbaHost.Scenario(s =>
            {
                s.Post.Url($"/accounts/{id}/deposit/25");
                s.StatusCodeShouldBe(202);
            }),
            timeoutInMilliseconds: 30_000);

        result.ShouldNotBeNull();

        // The existing Then vocabulary, verbatim — events the call caused, by value and by type.
        fixture.ThenEvents(new Deposited(id, 25m));
        fixture.ThenEventIsEmitted(typeof(Deposited), null);
        fixture.ThenMessagesSent<Deposit>();

        // The capture seam itself: the session records the work the call caused.
        fixture.LastExecution.Session.ShouldNotBeNull();
        fixture.LastExecution.Session.Executed.SingleMessage<Deposit>().ShouldNotBeNull();
        fixture.LastExecution.Error.ShouldBeNull();

        // And the read-model staleness wait composes with the tracked act exactly as it does with
        // WhenCommand: ThenDocument waits for the async daemon before loading (issue #211, item 4).
        await fixture.ThenDocument<AccountSummary>(summary =>
        {
            summary.Deposits.ShouldBe(1);
            summary.Balance.ShouldBe(25m);
        });
    }

    [PostgresFact]
    public async Task a_tracked_act_that_throws_is_captured_not_thrown()
    {
        await cleanSchema();
        await using var resource = hostResource();
        await resource.Start();
        var fixture = fixtureFor(resource);
        var id = Guid.NewGuid();

        await fixture.GivenEvents<Account>(id, new AccountOpened(id, "Bea"));

        // Alba's status assertion fails (404 from a route nobody mapped): captured into the
        // execution — the WhenCommand contract — so the sad-path Then steps can assert on it.
        var result = await fixture.WhenTracked(
            () => resource.AlbaHost.Scenario(s =>
            {
                s.Post.Url("/no/such/route");
                s.StatusCodeShouldBe(202);
            }));

        result.ShouldBeNull();
        fixture.LastExecution.Error.ShouldNotBeNull();
        fixture.LastExecution.Session.ShouldBeNull();
        fixture.LastExecution.NewEvents.ShouldBeEmpty();

        Should.Throw<SpecAssertionException>(() => fixture.ThenEvents(new Deposited(id, 25m)))
            .Message.ShouldContain("failed");
    }

    // --- host ----------------------------------------------------------------------------------

    /// <summary>
    /// The two-hop HTTP shape from issue #211: the endpoint publishes and returns 202; the handler
    /// appends; the daemon projects. Every downstream effect is asynchronous to the HTTP response.
    /// </summary>
    private static AlbaResource hostResource()
        => new(async () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddMarten(configureStore).AddAsyncDaemon(DaemonMode.Solo);
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<AccountHandler>();
            });
            return await AlbaHost.For(builder, app =>
            {
                app.MapPost("/accounts/{id:guid}/deposit/{amount:decimal}",
                    async (Guid id, decimal amount, IMessageBus bus) =>
                    {
                        await bus.PublishAsync(new Deposit(id, amount));
                        return Results.Accepted();
                    });
            });
        });

    private static void configureStore(StoreOptions options)
    {
        options.Connection(PostgresEnvironment.ConnectionString);
        options.DatabaseSchemaName = schema;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Projections.Snapshot<AccountSummary>(SnapshotLifecycle.Async);
    }

    private static async Task cleanSchema()
    {
        await using var store = DocumentStore.For(configureStore);
        await store.Advanced.Clean.DeleteAllDocumentsAsync();
        await store.Advanced.Clean.DeleteAllEventDataAsync();
    }

    private static HttpActFixture fixtureFor(IHostResource resource)
    {
        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>(null).Returns(resource);
        context.Cancellation.Returns(CancellationToken.None);

        var fixture = new HttpActFixture { Context = context };
        fixture.BeforeEach();
        return fixture;
    }
}
