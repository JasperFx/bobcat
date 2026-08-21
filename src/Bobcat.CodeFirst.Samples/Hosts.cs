using Bobcat.CritterStack;
using Bobcat.Runtime;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Marten;

namespace Bobcat.CodeFirst.Samples;

/// <summary>
/// The two hosts the ported tests ran against, registered once for the whole suite as named
/// <see cref="HostResource"/>s. Each original xUnit class stood its own host up per test class; here
/// the suite starts each host once and resets its state between scenarios, which is most of why
/// the ports run faster than the originals.
/// </summary>
public static class Hosts
{
    /// <summary>
    /// Wolverine dispatching into Marten with the Wolverine-Marten integration: the aggregate
    /// handler workflow, the Marten outbox and the order saga all ran against a host shaped like
    /// this. One schema, one durable local queue policy, handlers from all three ports.
    /// </summary>
    public const string App = "app";

    /// <summary>
    /// A Marten-only host with the async daemon: the projection port is pure Marten, and its
    /// conjoined tenancy is a store-wide setting the Wolverine host has no reason to carry.
    /// </summary>
    public const string Projections = "projections";

    public static HostResource AppHost() => new(
        () =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            builder.Services.AddMarten(marten =>
                {
                    marten.Connection(Postgres.ConnectionString);
                    marten.DatabaseSchemaName = "codefirst_app";
                    marten.AutoCreateSchemaObjects = AutoCreate.All;
                    marten.DisableNpgsqlLogging = true;

                    marten.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Inline);

                    // Straight from the original aggregate_handler_workflow test: its handlers mutate the
                    // FetchForWriting aggregate in place, and Marten 9's identity-map default would reuse
                    // that instance as the inline projection's baseline (JasperFx/wolverine#2857).
                    marten.Events.UseIdentityMapForAggregates = false;
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();

            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RaiseLetterHandler))
                    .IncludeType(typeof(RaiseIfValidatedHandler))
                    .IncludeType(typeof(ResponseHandler))
                    .IncludeType(typeof(OutboxedMessageHandler))
                    .IncludeType(typeof(Order));

                opts.Durability.Mode = DurabilityMode.Solo;

                // From MartenOutbox_end_to_end: the outbox only means something when the local queues are
                // durable, and the inbox keeps the handler side honest too.
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.ConfigureConventionalLocalRouting()
                    .CustomizeQueues((_, queue) => queue.UseDurableInbox());
            });

            builder.Services.AddResourceSetupOnStartup();
            return builder.Build();
        },
        name: App,
        reset: async host =>
        {
            // Events and documents through the store-agnostic Bobcat.CritterStack reset; Wolverine's
            // envelope storage through JasperFx's own stateful-resource contract.
            await host.ResetEventStoresAsync();
            await host.ClearStatefulResourcesAsync();
        });

    public static HostResource ProjectionsHost() => new(
        () =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            builder.Services.AddMarten(marten =>
                {
                    marten.Connection(Postgres.ConnectionString);
                    marten.DatabaseSchemaName = "codefirst_projections";
                    marten.AutoCreateSchemaObjects = AutoCreate.All;
                    marten.DisableNpgsqlLogging = true;

                    // The original build_aggregate_projection.simple_scenario store, verbatim.
                    marten.Events.StreamIdentity = StreamIdentity.AsString;
                    marten.Events.TenancyStyle = TenancyStyle.Conjoined;
                    marten.Projections.Snapshot<SimpleEntity>(SnapshotLifecycle.Async);
                })
                .AddAsyncDaemon(DaemonMode.Solo);

            return builder.Build();
        },
        name: Projections,
        // Marten's own reset: pauses the daemon, wipes, resumes — the daemon's high-water mark and the
        // data start over together, which is what makes "wait for non-stale" trustworthy afterwards.
        reset: host => host.ResetAllMartenDataAsync());
}
