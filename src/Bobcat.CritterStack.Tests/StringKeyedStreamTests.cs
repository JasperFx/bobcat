using Bobcat.Engine;
using Bobcat.Runtime;
using Fisher;
using Shouldly;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Wolverine;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// bobcat#177: <see cref="CritterStackFixture"/> against a store using STRING stream identity —
/// the shape Stoat's <c>{plan}/{nodeId}</c> claims and CritterWatch's service streams use, which
/// the fixture could not arrange at all while its surface was Guid-only. Drives the fixture's own
/// steps (not the lower-level context extensions, which already had string overloads) on a real
/// Fisher store with a real daemon, so every leg the Guid path exercises is proven for keys:
/// arrange, act with the tracked session, emitted-event capture, aggregate rebuild, and the
/// read-model load — the half most likely to regress silently, per the issue.
/// </summary>
public class StringKeyedStreamTests
{
    [Fact]
    public void the_grammar_given_step_reads_a_non_guid_id_as_a_stream_key()
    {
        var fixture = new RosterFixture { Context = Substitute.For<IStepContext>() };

        fixture.GivenNoEventsFor(typeof(Roster), "team/alpha");

        fixture.CurrentStreamKey.ShouldBe("team/alpha");
        fixture.CurrentStreamId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void the_grammar_given_step_still_reads_a_guid_id_as_a_guid()
    {
        var fixture = new RosterFixture { Context = Substitute.For<IStepContext>() };

        fixture.GivenNoEventsFor(typeof(Roster), "11111111-1111-1111-1111-111111111111");

        fixture.CurrentStreamKey.ShouldBeNull();
        fixture.CurrentStreamId.ShouldBe(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public async Task arrange_act_and_read_model_all_work_over_a_string_keyed_stream()
    {
        await using var database = TemporarySqliteDatabase.Create();
        await using var resource = hostResource(database);
        await resource.Start();

        var fixture = new RosterFixture { Context = contextFor(resource) };
        fixture.BeforeEach();

        // Arrange through the typed string-key steps.
        await fixture.GivenEvents<Roster>("team/alpha", new RosterOpened("team/alpha", "Alpha"));

        // Act through the bus with the tracked session, exactly as the Guid path does.
        var run = await fixture.WhenCommand<Roster>(new JoinTeam("team/alpha", "Ann"));

        run.ShouldNotBeNull();
        run.NewEvents.Select(e => e.Data).OfType<MemberJoined>().ShouldHaveSingleItem().Name.ShouldBe("Ann");
        run.Aggregate.ShouldNotBeNull().Members.ShouldBe(1);

        // The read-model leg: loads by the STRING id after the projection wait.
        await fixture.ThenDocument<RosterSummary>(doc =>
        {
            doc.Id.ShouldBe("team/alpha");
            doc.Members.ShouldBe(1);
        });
    }

    // --- host ----------------------------------------------------------------------------------

    private static HostResource hostResource(TemporarySqliteDatabase database)
        => new(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddFisher(options =>
            {
                options.Connection(database.ConnectionString);
                options.AutoCreateSchemaObjects = AutoCreate.All;
                // The whole point of this class: streams identified by string keys.
                options.Events.StreamIdentity = StreamIdentity.AsString;
                options.Projections.Snapshot<RosterSummary>(SnapshotLifecycle.Async);
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<RosterHandler>();
            });
            return builder.Build();
        });

    private static IStepContext contextFor(IHostResource resource)
    {
        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>(null).Returns(resource);
        context.Cancellation.Returns(CancellationToken.None);
        return context;
    }

    /// <summary>Exposes the protected identity slots so the two grammar-step tests can assert on them.</summary>
    private class RosterFixture : CritterStackFixture
    {
        public Guid CurrentStreamId => StreamId;
        public string? CurrentStreamKey => StreamKey;
    }
}

// --- the string-keyed test domain --------------------------------------------------------------

public record RosterOpened(string RosterId, string Team);
public record MemberJoined(string RosterId, string Name);
public record JoinTeam(string RosterId, string Name);

public class Roster
{
    public string Id { get; set; } = string.Empty;
    public string Team { get; set; } = string.Empty;
    public int Members { get; set; }

    public void Apply(RosterOpened e)
    {
        Id = e.RosterId;
        Team = e.Team;
    }

    public void Apply(MemberJoined e) => Members++;
}

/// <summary>Async self-aggregating snapshot, so the read-model leg exercises the daemon wait.</summary>
public class RosterSummary
{
    public string Id { get; set; } = string.Empty;
    public int Members { get; set; }

    public void Apply(RosterOpened e) => Id = e.RosterId;
    public void Apply(MemberJoined e) => Members++;
}

/// <summary>Same deliberate shape as <see cref="FisherAccountHandler"/>: saves its own session,
/// no WolverineFx.Fisher integration, appending by the STRING key.</summary>
public class RosterHandler
{
    public static async Task Handle(JoinTeam command, IDocumentStore store)
    {
        await using var session = store.LightweightSession();
        session.Events.Append(command.RosterId, new MemberJoined(command.RosterId, command.Name));
        await session.SaveChangesAsync();
    }
}
