using Bobcat.Engine;
using Bobcat.Runtime;
using JasperFx.Events;
using Marten;

namespace Bobcat.Marten;

/// <summary>
/// <see cref="IStepContext"/> extension methods that delegate to the registered
/// <see cref="IMartenResource"/>, so fixture steps can do Marten work without holding a
/// direct reference to the <see cref="IDocumentStore"/>. Each helper opens its own
/// short-lived session per call, as Marten's design encourages.
/// </summary>
public static class MartenStepContextExtensions
{
    private static IDocumentStore Store(IStepContext context, string? resourceName)
        => context.GetResource<IMartenResource>(resourceName).DocumentStore;

    // --- Document operations ---

    public static async Task<T?> QueryByIdAsync<T>(this IStepContext context, object id, string? resourceName = null)
        where T : notnull
    {
        await using var session = Store(context, resourceName).QuerySession();
        return id switch
        {
            Guid g => await session.LoadAsync<T>(g),
            int i => await session.LoadAsync<T>(i),
            long l => await session.LoadAsync<T>(l),
            string s => await session.LoadAsync<T>(s),
            _ => throw new ArgumentException($"Unsupported Marten identity type '{id.GetType().Name}'.", nameof(id))
        };
    }

    public static async Task<IReadOnlyList<T>> QueryAllAsync<T>(this IStepContext context, string? resourceName = null)
    {
        await using var session = Store(context, resourceName).QuerySession();
        return await session.Query<T>().ToListAsync();
    }

    public static async Task StoreAsync<T>(this IStepContext context, T document, string? resourceName = null)
        where T : notnull
    {
        await using var session = Store(context, resourceName).LightweightSession();
        session.Store(document);
        await session.SaveChangesAsync();
    }

    public static async Task DeleteByIdAsync<T>(this IStepContext context, object id, string? resourceName = null)
        where T : notnull
    {
        await using var session = Store(context, resourceName).LightweightSession();
        switch (id)
        {
            case Guid g: session.Delete<T>(g); break;
            case int i: session.Delete<T>(i); break;
            case long l: session.Delete<T>(l); break;
            case string s: session.Delete<T>(s); break;
            default: throw new ArgumentException($"Unsupported Marten identity type '{id.GetType().Name}'.", nameof(id));
        }
        await session.SaveChangesAsync();
    }

    // --- Event sourcing ---

    public static async Task<IReadOnlyList<IEvent>> FetchStreamAsync(this IStepContext context, Guid streamId, string? resourceName = null)
    {
        await using var session = Store(context, resourceName).QuerySession();
        return await session.Events.FetchStreamAsync(streamId);
    }

    public static async Task<T?> AggregateStreamAsync<T>(this IStepContext context, Guid streamId, string? resourceName = null)
        where T : class
    {
        await using var session = Store(context, resourceName).QuerySession();
        return await session.Events.AggregateStreamAsync<T>(streamId);
    }

    // --- Hygiene helpers (between-scenario reset, or dedicated [Given] clean steps) ---

    public static Task CleanAllMartenDataAsync(this IStepContext context, string? resourceName = null)
        => CleanAllMartenDataAsync(Store(context, resourceName));

    public static async Task CompletelyRemoveAllAsync(this IStepContext context, string? resourceName = null)
        => await Store(context, resourceName).Advanced.Clean.CompletelyRemoveAllAsync();

    internal static async Task CleanAllMartenDataAsync(IDocumentStore store)
    {
        await store.Advanced.Clean.DeleteAllDocumentsAsync();
        await store.Advanced.Clean.DeleteAllEventDataAsync();
    }
}
