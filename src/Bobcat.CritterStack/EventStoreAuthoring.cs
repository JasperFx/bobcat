using System.Reflection;
using JasperFx.Events;

namespace Bobcat.CritterStack;

/// <summary>
/// The two write-side / document-side operations a spec needs that <c>JasperFx.Events</c> 2.37.0
/// has no abstraction for — <b>appending</b> arrange-events to a stream, and <b>loading</b> a
/// projected read-model document by id. Both go through the convention Marten, Polecat and Fisher
/// share, reached without a reference to any of them, exactly as <see cref="EventStores"/>' aggregate
/// and reset helpers do. When JasperFx.Events grows the abstraction, this file is what gets deleted.
/// </summary>
/// <remarks>
/// A session is opened through the store's <c>IEventStore&lt;TOperations, TQuerySession&gt;</c> closure
/// (see <see cref="EventStoreSessions"/>); its <c>Events</c> member is the <see cref="IEventOperations"/>
/// write surface, and <c>SaveChangesAsync(CancellationToken)</c> / <c>LoadAsync&lt;T&gt;(id, ct)</c> are
/// found by name — the shape all three stores share. A store matching none gets an exception naming
/// what was looked for rather than a silent pass.
/// </remarks>
internal static class EventStoreAuthoring
{
    /// <summary>
    /// Append <paramref name="events"/> to the stream, starting it (as an
    /// <paramref name="aggregateType"/> stream) when it does not exist yet and appending when it does.
    /// This is the arrange half of an event-sourcing spec — the prior events a command runs against.
    /// </summary>
    public static async Task AppendAsync(IEventStore store, Type aggregateType, object streamIdentity,
        IReadOnlyList<object> events, CancellationToken token = default)
    {
        if (events.Count == 0) return;

        // Both stream identity kinds, dispatched here so the fixture stays polymorphic
        // (bobcat#177): a Guid-identified store gets the Guid overloads, a string-keyed one
        // (Stoat, CritterWatch) the streamKey overloads. Anything else is a wiring mistake.
        var existing = streamIdentity switch
        {
            Guid guid => await EventStores.FetchStreamAsync(store, guid, token).ConfigureAwait(false),
            string key => await EventStores.FetchStreamAsync(store, key, token).ConfigureAwait(false),
            _ => throw new ArgumentException(
                $"A stream identity must be a Guid or a string, not {streamIdentity.GetType().Name}.", nameof(streamIdentity)),
        };

        var session = await EventStoreSessions.OpenAsync(store).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            var operations = eventOperationsOf(session);

            if (existing.Count == 0)
            {
                if (streamIdentity is Guid guid) operations.StartStream(aggregateType, guid, events.ToArray());
                else operations.StartStream(aggregateType, (string)streamIdentity, events.ToArray());
            }
            else
            {
                if (streamIdentity is Guid guid) operations.Append(guid, events.ToArray());
                else operations.Append((string)streamIdentity, events.ToArray());
            }

            await saveChangesAsync(session, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Load a projected read-model document of type <typeparamref name="T"/> by id, through the
    /// session's <c>LoadAsync&lt;T&gt;(id, ct)</c>. <paramref name="id"/> may be a <see cref="Guid"/> or a
    /// string key. Returns null when no such document exists.
    /// </summary>
    public static async Task<T?> LoadDocumentAsync<T>(IEventStore store, object id, CancellationToken token = default)
        where T : class
    {
        var session = await EventStoreSessions.OpenAsync(store).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            var load = loadMethod(session.GetType(), typeof(T), id.GetType());
            var task = (Task)load.Invoke(session, [id, token])!;
            await task.ConfigureAwait(false);

            // Task<T?>.Result via the compile-time-unknown closed generic.
            var resultProperty = task.GetType().GetProperty("Result")!;
            return (T?)resultProperty.GetValue(task);
        }
    }

    private static IEventOperations eventOperationsOf(object session)
    {
        if (session is IEventOperations direct) return direct;

        var property = session.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Concat(session.GetType().GetInterfaces().SelectMany(i => i.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
            .FirstOrDefault(p => p.Name == "Events" && typeof(IEventOperations).IsAssignableFrom(p.PropertyType));

        if (property?.GetValue(session) is IEventOperations events) return events;

        throw new InvalidOperationException(
            $"Cannot append events through session type {session.GetType().FullName}: it is neither a " +
            "JasperFx.Events.IEventOperations nor exposes a public 'Events' member that is one. Marten, Polecat and " +
            "Fisher sessions all do.");
    }

    private static async Task saveChangesAsync(object session, CancellationToken token)
    {
        var method = session.GetType().GetMethod("SaveChangesAsync",
                         BindingFlags.Public | BindingFlags.Instance, [typeof(CancellationToken)])
                     ?? throw new InvalidOperationException(
                         $"Session type {session.GetType().FullName} has no public SaveChangesAsync(CancellationToken); " +
                         "Bobcat cannot commit the arrange-events it appended. Marten, Polecat and Fisher sessions all have one.");

        await ((Task)method.Invoke(session, [token])!).ConfigureAwait(false);
    }

    private static MethodInfo loadMethod(Type sessionType, Type documentType, Type idType)
    {
        var candidate = sessionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "LoadAsync"
                                 && m.IsGenericMethodDefinition
                                 && m.GetGenericArguments().Length == 1
                                 && m.GetParameters().Length == 2
                                 && m.GetParameters()[0].ParameterType == idType
                                 && m.GetParameters()[1].ParameterType == typeof(CancellationToken))
            ?? throw new InvalidOperationException(
                $"Session type {sessionType.FullName} has no public LoadAsync<T>({idType.Name}, CancellationToken) to " +
                "load a read-model document by id. Marten and Polecat sessions have one; is the id type right " +
                "(Guid vs string)?");

        return candidate.MakeGenericMethod(documentType);
    }
}
