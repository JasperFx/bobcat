using JasperFx.CommandLine.Descriptions;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bobcat.CritterStack;

/// <summary>
/// Host-level Critter Stack helpers — the same operations as the <c>IStepContext</c> extensions,
/// but hanging off the <see cref="IHost"/> / <see cref="IServiceProvider"/> so a host resource's
/// reset hook, a <c>BeforeAll</c>, or a global action can use them before any scenario scope exists.
/// </summary>
/// <remarks>
/// Marten (<c>AddMarten</c>), Polecat (<c>AddPolecat</c>) and Fisher (<c>AddFisher</c>) all
/// register their store as <see cref="IEventStore"/>, so <see cref="EventStores(IServiceProvider)"/>
/// is how a spec project finds the store without referencing the store's package.
/// </remarks>
public static class CritterStackHostExtensions
{
    /// <summary>Every event store registered in the container, in registration order.</summary>
    public static IReadOnlyList<IEventStore> EventStores(this IServiceProvider services)
        => services.GetServices<IEventStore>().ToList();

    /// <summary>
    /// The one event store registered in the container, or the one whose
    /// <see cref="IEventStore.Identity"/> name matches <paramref name="storeName"/> when several are
    /// (Marten's <c>AddMartenStore&lt;T&gt;</c>, Fisher's <c>AddFisherStore&lt;T&gt;</c>).
    /// </summary>
    public static IEventStore EventStore(this IServiceProvider services, string? storeName = null)
    {
        var stores = services.EventStores();

        if (stores.Count == 0)
        {
            throw new InvalidOperationException(
                "No JasperFx.Events.IEventStore is registered in the host's container. Marten (AddMarten), Polecat " +
                "(AddPolecat) and Fisher (AddFisher) all register one — is the store wired into this host?");
        }

        if (storeName == null)
        {
            if (stores.Count == 1)
                return stores[0];

            var names = string.Join(", ", stores.Select(Bobcat.CritterStack.EventStores.Describe));
            throw new InvalidOperationException(
                $"The host registers {stores.Count} event stores ({names}). Pass storeName to choose one.");
        }

        return stores.FirstOrDefault(s => matches(s, storeName))
               ?? throw new InvalidOperationException(
                   $"No event store named '{storeName}' is registered. Known: " +
                   string.Join(", ", stores.Select(Bobcat.CritterStack.EventStores.Describe)) + ".");
    }

    /// <summary>The coordinator driving the host's projection daemon, when the host runs one.</summary>
    public static IProjectionCoordinator? ProjectionCoordinator(this IServiceProvider services)
        => services.GetService<IProjectionCoordinator>();

    /// <summary>
    /// Delete every event and every document in every event store the host registers, keeping the
    /// schema — the between-scenario reset. Pass this as a <c>HostResource</c> / <c>AlbaResource</c>
    /// reset hook: <c>new AlbaResource&lt;Program&gt;(reset: host =&gt; host.ResetEventStoresAsync())</c>.
    /// </summary>
    public static async Task ResetEventStoresAsync(this IServiceProvider services, CancellationToken token = default)
    {
        foreach (var store in services.EventStores())
            await Bobcat.CritterStack.EventStores.ResetAllDataAsync(store, token).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ResetEventStoresAsync(IServiceProvider, CancellationToken)"/>
    public static Task ResetEventStoresAsync(this IHost host, CancellationToken token = default)
        => host.Services.ResetEventStoresAsync(token);

    /// <summary>
    /// Run <see cref="IStatefulResource.ClearState"/> on every stateful resource the host's
    /// <see cref="ISystemPart"/>s describe — Wolverine's envelope storage and transports, and any store
    /// whose database supports rewindable state. This is what <c>jasperfx resources clear</c> does,
    /// minus the schema <c>Setup</c> that command also runs per resource. It does <b>not</b> replace
    /// <see cref="ResetEventStoresAsync(IServiceProvider, CancellationToken)"/>: Marten 9.22's database
    /// resource reports no rewindable state, so its <c>ClearState</c> is a no-op.
    /// </summary>
    public static async Task ClearStatefulResourcesAsync(this IServiceProvider services, CancellationToken token = default)
    {
        foreach (var part in services.GetServices<ISystemPart>())
        {
            foreach (var resource in await part.FindResources().ConfigureAwait(false))
                await resource.ClearState(token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="ClearStatefulResourcesAsync(IServiceProvider, CancellationToken)"/>
    public static Task ClearStatefulResourcesAsync(this IHost host, CancellationToken token = default)
        => host.Services.ClearStatefulResourcesAsync(token);

    /// <summary>
    /// Wait for every async projection on the named (or only) store to catch up with its high-water
    /// mark. See <see cref="Bobcat.CritterStack.EventStores.WaitForNonStaleProjectionsAsync"/>.
    /// </summary>
    public static Task WaitForNonStaleProjectionsAsync(
        this IServiceProvider services,
        TimeSpan? timeout = null,
        string? storeName = null,
        CancellationToken token = default)
        => Bobcat.CritterStack.EventStores.WaitForNonStaleProjectionsAsync(
            services.EventStore(storeName), timeout, services.ProjectionCoordinator(), token);

    /// <inheritdoc cref="WaitForNonStaleProjectionsAsync(IServiceProvider, TimeSpan?, string?, CancellationToken)"/>
    public static Task WaitForNonStaleProjectionsAsync(
        this IHost host,
        TimeSpan? timeout = null,
        string? storeName = null,
        CancellationToken token = default)
        => host.Services.WaitForNonStaleProjectionsAsync(timeout, storeName, token);

    private static bool matches(IEventStore store, string storeName)
    {
        try
        {
            return string.Equals(store.Identity.Name, storeName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
