using System.Diagnostics;
using System.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Bobcat.CritterStack;

/// <summary>
/// The store-agnostic binding: every event-store operation Bobcat's Critter Stack helpers need,
/// expressed against the <c>JasperFx.Events</c> abstractions an <see cref="IEventStore"/> exposes
/// rather than against Marten, Polecat or Fisher. Marten, Polecat and Fisher all register their
/// store as <see cref="IEventStore"/> in the host container, which is how the
/// <see cref="CritterStackStepContextExtensions"/> find one without a compile-time reference to any
/// of them.
/// </summary>
/// <remarks>
/// <para>
/// Two operations have no JasperFx.Events abstraction in the version Bobcat pins (2.53.0), and are
/// reached through the convention the three stores share rather than through an interface:
/// </para>
/// <list type="bullet">
/// <item><b>Rebuilding an aggregate</b> — <see cref="IReadOnlyEventStore"/> can fetch a stream but not
/// aggregate it. Marten and Polecat hand back their session's <see cref="IQueryEventStore"/> from
/// <see cref="IEventStore.OpenReadOnlyEventStore"/>, which can; Fisher does not, so the fallback opens
/// a session through the store's <c>IEventStore&lt;TOperations, TQuerySession&gt;</c> closure and finds
/// the session's <c>Events</c> member. One reflective lookup, cached per store type.</item>
/// <item><b>Wiping data between scenarios</b> — JasperFx's <c>IStatefulResource.ClearState</c> is the
/// abstraction, but Marten 9.22's database does not implement <c>IDatabaseWithRewindableState</c>, so
/// <c>jasperfx resources clear</c> is a no-op for it. The stores do agree on
/// <c>store.Advanced.ResetAllData(ct)</c> (Fisher spells it <c>ResetAllDataAsync</c>) and on
/// <c>store.Advanced.Clean.DeleteAllEventDataAsync()</c> / <c>DeleteAllDocumentsAsync()</c>, so
/// <see cref="ResetAllDataAsync"/> follows that convention and says exactly what it looked for when a
/// store matches neither.</item>
/// </list>
/// <para>
/// Both are the bounded, documented softening of "bind to the abstractions" — the same bargain as
/// <c>GrammarBehaviors.Resolve</c> in core. When JasperFx.Events grows the abstraction, the
/// convention path is the one line to delete. Re-checked against 2.53.0 (issue #103): neither has
/// landed, and both branches are now proved on the real stores — Marten takes the
/// <see cref="IQueryEventStore"/> / <c>ResetAllData</c> branch, Fisher the session-closure /
/// <c>ResetAllDataAsync</c> one.
/// </para>
/// <para>
/// <b>Why the projection waits do not delegate to <c>ProjectionScenario&lt;,&gt;</c></b>
/// (<c>JasperFx.Events.TestSupport</c>, in the pinned version): it is a scripted harness that wipes
/// the store, builds its <i>own</i> daemon and appends through its <i>own</i> session, reached only
/// through each store's <c>Advanced</c> member and closed over that store's session pair — there is
/// no non-generic interface and no <see cref="IEventStore"/> accessor. A spec appends through the
/// application under test and waits on the host's daemon, so the right tool is the wait the
/// scenario itself performs after each batch, which is what <see cref="WaitForNonStaleProjectionsAsync"/>
/// calls directly.
/// </para>
/// </remarks>
public static class EventStores
{
    public static readonly TimeSpan DefaultProjectionTimeout = TimeSpan.FromSeconds(5);

    // --- Reading --------------------------------------------------------------------------

    /// <summary>All events in a <see cref="Guid"/>-identified stream, through the store's read-only view.</summary>
    public static Task<IReadOnlyList<IEvent>> FetchStreamAsync(IEventStore store, Guid streamId, CancellationToken token = default)
        => fetchStreamAsync(store, reader => reader.FetchStreamAsync(streamId, token: token));

    /// <summary>All events in a string-keyed stream, through the store's read-only view.</summary>
    public static Task<IReadOnlyList<IEvent>> FetchStreamAsync(IEventStore store, string streamKey, CancellationToken token = default)
        => fetchStreamAsync(store, reader => reader.FetchStreamAsync(streamKey, token: token));

    /// <summary>Rebuild the aggregate of a <see cref="Guid"/>-identified stream from its events.</summary>
    public static Task<T?> AggregateStreamAsync<T>(IEventStore store, Guid streamId, CancellationToken token = default) where T : class
        => aggregateStreamAsync(store, events => events.AggregateStreamAsync<T>(streamId, token: token));

    /// <summary>Rebuild the aggregate of a string-keyed stream from its events.</summary>
    public static Task<T?> AggregateStreamAsync<T>(IEventStore store, string streamKey, CancellationToken token = default) where T : class
        => aggregateStreamAsync(store, events => events.AggregateStreamAsync<T>(streamKey, token: token));

    private static async Task<IReadOnlyList<IEvent>> fetchStreamAsync(
        IEventStore store,
        Func<IReadOnlyEventStore, Task<IReadOnlyList<IEvent>>> read)
    {
        var reader = store.OpenReadOnlyEventStore();
        try
        {
            return await read(reader).ConfigureAwait(false);
        }
        finally
        {
            await disposeIfOwned(reader).ConfigureAwait(false);
        }
    }

    private static async Task<T?> aggregateStreamAsync<T>(IEventStore store, Func<IQueryEventStore, Task<T?>> aggregate)
        where T : class
    {
        // Marten and Polecat: the read-only view IS the session's IQueryEventStore, so no reflection.
        var reader = store.OpenReadOnlyEventStore();
        try
        {
            if (reader is IQueryEventStore querying)
                return await aggregate(querying).ConfigureAwait(false);
        }
        finally
        {
            await disposeIfOwned(reader).ConfigureAwait(false);
        }

        // Fisher (and any store whose read-only view is a separate object): open a real session
        // through the generic closure and aggregate through its Events member.
        var session = await EventStoreSessions.OpenAsync(store).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            return await aggregate(EventStoreSessions.QueryEventStoreOf(session)).ConfigureAwait(false);
        }
    }

    // --- Projections ----------------------------------------------------------------------

    /// <summary>
    /// Block until every asynchronous projection on every database of the store has caught up with
    /// the store's high-water mark, or <paramref name="timeout"/> elapses. Returns at once when the
    /// store has no async projections. This is the same wait <c>ProjectionScenario</c> performs after
    /// each batch of appends, reached through <see cref="IEventDatabase.WaitForNonStaleProjectionDataAsync"/>
    /// so it does not matter who runs the daemon — Marten's coordinator, Fisher's hosted service, or
    /// Wolverine.
    /// </summary>
    /// <param name="coordinator">
    /// Optional fallback for a store that does not enumerate its databases through
    /// <see cref="IEventStore.AllDatabases"/>: the host's <see cref="IProjectionCoordinator"/>, whose
    /// main-database daemon is asked to <see cref="IProjectionDaemon.WaitForNonStaleData"/> instead.
    /// </param>
    public static async Task WaitForNonStaleProjectionsAsync(
        IEventStore store,
        TimeSpan? timeout = null,
        IProjectionCoordinator? coordinator = null,
        CancellationToken token = default)
    {
        var wait = timeout ?? DefaultProjectionTimeout;
        var databases = await store.AllDatabases().ConfigureAwait(false);

        if (databases.Count > 0)
        {
            foreach (var database in databases)
                await database.WaitForNonStaleProjectionDataAsync(wait).WaitAsync(token).ConfigureAwait(false);

            return;
        }

        if (coordinator != null)
        {
            await coordinator.DaemonForMainDatabase().WaitForNonStaleData(wait).WaitAsync(token).ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException(
            $"Event store {Describe(store)} enumerates no databases through IEventStore.AllDatabases() and the host " +
            "registers no IProjectionCoordinator, so there is nothing to wait on. Marten, Polecat and Fisher all " +
            "enumerate their databases; if this is another store, register its IProjectionCoordinator.");
    }

    /// <summary>
    /// Wait until the async projection shards whose name contains <paramref name="projectionName"/>
    /// (default: <c>typeof(T).Name</c>, which matches both a projection class and the view type a
    /// <c>Snapshot&lt;T&gt;</c> registers) have reached the store's current highest event sequence.
    /// Returns at once when the store has no async shards at all — an inline projection has nothing
    /// to wait for — but a name that matches none of the shards that do exist throws rather than
    /// passing vacuously.
    /// </summary>
    public static async Task WaitForProjectionAsync<T>(
        IEventStore store,
        TimeSpan? timeout = null,
        string? projectionName = null,
        CancellationToken token = default)
    {
        var wait = timeout ?? DefaultProjectionTimeout;
        var name = projectionName ?? typeof(T).Name;

        var shards = EventStoreShards.TryNames(store);
        foreach (var database in await store.AllDatabases().ConfigureAwait(false))
        {
            var target = await database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);
            await waitForShards(database, shards, name, target, wait, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wait until the async projection shards whose name contains <paramref name="projectionName"/>
    /// (default: <c>typeof(T).Name</c>) have processed at least event sequence
    /// <paramref name="minSequence"/>. Same matching and vacuity rules as
    /// <see cref="WaitForProjectionAsync{T}(IEventStore, TimeSpan?, string?, CancellationToken)"/>.
    /// </summary>
    public static async Task WaitForProjectionAsync<T>(
        IEventStore store,
        long minSequence,
        TimeSpan? timeout = null,
        string? projectionName = null,
        CancellationToken token = default)
    {
        var wait = timeout ?? DefaultProjectionTimeout;
        var name = projectionName ?? typeof(T).Name;

        var shards = EventStoreShards.TryNames(store);
        foreach (var database in await store.AllDatabases().ConfigureAwait(false))
            await waitForShards(database, shards, name, minSequence, wait, token).ConfigureAwait(false);
    }

    /// <summary>The current progress of every async projection shard, per database.</summary>
    public static async Task<IReadOnlyList<ShardState>> ProjectionProgressAsync(IEventStore store, CancellationToken token = default)
    {
        var all = new List<ShardState>();
        foreach (var database in await store.AllDatabases().ConfigureAwait(false))
            all.AddRange(await database.AllProjectionProgress(token).ConfigureAwait(false));

        return all;
    }

    /// <param name="configured">
    /// The store's configured async shard identities, when the store's generic closure could be read;
    /// null means "unknown", in which case the progress rows are the only evidence there is.
    /// </param>
    private static async Task waitForShards(
        IEventDatabase database,
        IReadOnlyList<string>? configured,
        string name,
        long target,
        TimeSpan timeout,
        CancellationToken token)
    {
        // Decide what to wait on from the store's CONFIGURATION where we can, not from the progress
        // table: a daemon writes a shard's progress row only after its first batch, so right after
        // the first append an empty table is indistinguishable from "no async projections" and a wait
        // keyed on it would pass vacuously.
        IReadOnlyList<string>? expected = null;
        if (configured != null)
        {
            if (configured.Count == 0)
                return; // no async shards at all — nothing can be stale

            expected = configured.Where(n => n.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (expected.Count == 0)
                throw noShardMatches(database, name, configured);
        }

        var interval = TimeSpan.FromMilliseconds(50);
        var maxInterval = TimeSpan.FromSeconds(1);
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            var progress = await database.AllProjectionProgress(token).ConfigureAwait(false);
            var reported = progress.Where(p => !isHighWaterShard(p)).ToList();

            IReadOnlyList<(string Shard, long Sequence)> matching;
            if (expected != null)
            {
                // A shard with no row yet has processed nothing.
                matching = expected
                    .Select(shard => (shard, reported.FirstOrDefault(p => p.ShardName.Equals(shard, StringComparison.OrdinalIgnoreCase))?.Sequence ?? 0L))
                    .ToList();
            }
            else
            {
                if (reported.Count == 0)
                    return; // unknown configuration and nothing reported — the most we can say

                matching = reported
                    .Where(p => p.ShardName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (p.ShardName, p.Sequence))
                    .ToList();

                if (matching.Count == 0)
                    throw noShardMatches(database, name, reported.Select(p => p.ShardName).ToList());
            }

            if (matching.All(p => p.Sequence >= target))
                return;

            if (stopwatch.Elapsed >= timeout)
            {
                var state = string.Join(", ", matching.Select(p => $"'{p.Shard}' at {p.Sequence}"));
                throw new TimeoutException(
                    $"Projection '{name}' did not reach sequence {target} within {timeout.TotalMilliseconds}ms ({state}).");
            }

            await Task.Delay(interval, token).ConfigureAwait(false);
            interval = TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds * 2, maxInterval.TotalMilliseconds));
        }
    }

    private static InvalidOperationException noShardMatches(IEventDatabase database, string name, IReadOnlyList<string> known)
        => new(
            $"No async projection shard on database '{database.Identifier}' matches '{name}'. Known shards: " +
            string.Join(", ", known.Select(s => $"'{s}'")) +
            ". Pass projectionName explicitly if the projection is not named after its type.");

    // The high-water mark is itself reported as a shard by Marten; it is the thing being waited FOR,
    // never a thing to wait ON.
    private static bool isHighWaterShard(ShardState state)
        => string.Equals(state.ShardName, ShardState.HighWaterMark, StringComparison.OrdinalIgnoreCase);

    // --- Resetting ------------------------------------------------------------------------

    /// <summary>
    /// Delete every event and every document in the store, keeping its schema — the between-scenario
    /// reset. Follows the convention the Critter Stack stores share (<c>Advanced.ResetAllData</c>,
    /// or <c>Advanced.Clean.DeleteAllEventDataAsync</c> + <c>DeleteAllDocumentsAsync</c>); see the
    /// class remarks for why this is a convention rather than an interface today.
    /// </summary>
    public static Task ResetAllDataAsync(IEventStore store, CancellationToken token = default)
        => StoreResetConvention.ResetAsync(store, token);

    // --- Plumbing ---------------------------------------------------------------------------

    internal static string Describe(IEventStore store)
    {
        try
        {
            return $"'{store.Identity.Name}' ({store.Identity.Type}, {store.GetType().Name})";
        }
        catch
        {
            return $"of type {store.GetType().FullName}";
        }
    }

    private static ValueTask disposeIfOwned(object? reader) => reader switch
    {
        IAsyncDisposable asyncDisposable => asyncDisposable.DisposeAsync(),
        IDisposable disposable => dispose(disposable),
        _ => ValueTask.CompletedTask
    };

    private static ValueTask dispose(IDisposable disposable)
    {
        disposable.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Opens a store session through the <c>IEventStore&lt;TOperations, TQuerySession&gt;</c> closure
/// without knowing the closure at compile time, and finds the session's
/// <see cref="IQueryEventStore"/>. The one reflective seam in the package; see
/// <see cref="EventStores"/> remarks.
/// </summary>
internal static class EventStoreSessions
{
    private static readonly Dictionary<Type, MethodInfo> openSessionByStoreType = new();
    private static readonly Dictionary<Type, PropertyInfo?> eventsBySessionType = new();
    private static readonly Lock gate = new();

    public static async Task<IAsyncDisposable> OpenAsync(IEventStore store)
    {
        var open = openSessionMethod(store.GetType());

        var databases = await store.AllDatabases().ConfigureAwait(false);
        if (databases.Count == 0)
        {
            throw new InvalidOperationException(
                $"Event store {EventStores.Describe(store)} enumerates no databases through IEventStore.AllDatabases(), " +
                "so no session can be opened against it to aggregate a stream.");
        }

        var session = open.Invoke(store, [databases[0]])
                      ?? throw new InvalidOperationException($"OpenSession on {store.GetType().Name} returned null.");

        return session as IAsyncDisposable
               ?? throw new InvalidOperationException(
                   $"The session type {session.GetType().Name} opened by {store.GetType().Name} is not IAsyncDisposable, " +
                   "which every IStorageOperations is expected to be.");
    }

    public static IQueryEventStore QueryEventStoreOf(object session)
    {
        if (session is IQueryEventStore direct)
            return direct;

        var property = eventsProperty(session.GetType());
        var value = property?.GetValue(session);
        if (value is IQueryEventStore events)
            return events;

        throw new InvalidOperationException(
            $"Cannot aggregate a stream through session type {session.GetType().FullName}: it is not a " +
            "JasperFx.Events.IQueryEventStore and has no public 'Events' member that is one. Marten, Polecat and Fisher " +
            "sessions all expose one; another store needs IEventStore.OpenReadOnlyEventStore() to return an IQueryEventStore.");
    }

    private static MethodInfo openSessionMethod(Type storeType)
    {
        lock (gate)
        {
            if (openSessionByStoreType.TryGetValue(storeType, out var cached))
                return cached;

            var closure = storeType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventStore<,>))
                ?? throw new InvalidOperationException(
                    $"Event store type {storeType.FullName} does not implement IEventStore<TOperations, TQuerySession>, " +
                    "so Bobcat cannot open a session on it to aggregate a stream.");

            var method = closure.GetMethods()
                .First(m => m.Name == nameof(IEventStore<IStorageOperations, object>.OpenSession)
                            && m.GetParameters().Length == 1
                            && m.GetParameters()[0].ParameterType == typeof(IEventDatabase));

            openSessionByStoreType[storeType] = method;
            return method;
        }
    }

    private static PropertyInfo? eventsProperty(Type sessionType)
    {
        lock (gate)
        {
            if (eventsBySessionType.TryGetValue(sessionType, out var cached))
                return cached;

            // The session's own public surface first, then every interface it implements — Fisher's
            // IQueryEventStore-typed Events is an explicit interface implementation.
            var candidates = new[] { sessionType }.Concat(sessionType.GetInterfaces());
            var property = candidates
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .FirstOrDefault(p => p.Name == "Events" && typeof(IQueryEventStore).IsAssignableFrom(p.PropertyType))
                ?? candidates
                    .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    .FirstOrDefault(p => p.Name == "Events" && p.GetIndexParameters().Length == 0);

            eventsBySessionType[sessionType] = property;
            return property;
        }
    }
}

/// <summary>
/// Reads the store's configured async shard identities through the
/// <c>IEventStore&lt;TOperations, TQuerySession&gt;.AllShards()</c> closure, so a projection wait can
/// tell "no async projections" from "the daemon has not written a progress row yet". Same bounded
/// reflection as <see cref="EventStoreSessions"/>.
/// </summary>
internal static class EventStoreShards
{
    private static readonly Dictionary<Type, MethodInfo?> allShardsByStoreType = new();
    private static readonly Lock gate = new();

    /// <summary>Shard identities, or null when the store does not expose the generic closure.</summary>
    public static IReadOnlyList<string>? TryNames(IEventStore store)
    {
        var allShards = allShardsMethod(store.GetType());
        if (allShards == null)
            return null;

        if (allShards.Invoke(store, []) is not System.Collections.IEnumerable shards)
            return null;

        var names = new List<string>();
        foreach (var shard in shards)
        {
            if (shard == null) continue;
            var name = shard.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(shard);
            if (name is ShardName shardName)
                names.Add(shardName.Identity);
        }

        return names;
    }

    private static MethodInfo? allShardsMethod(Type storeType)
    {
        lock (gate)
        {
            if (allShardsByStoreType.TryGetValue(storeType, out var cached))
                return cached;

            var closure = storeType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventStore<,>));

            var method = closure?.GetMethods()
                .FirstOrDefault(m => m.Name == nameof(IEventStore<IStorageOperations, object>.AllShards) && m.GetParameters().Length == 0);

            allShardsByStoreType[storeType] = method;
            return method;
        }
    }
}

/// <summary>
/// The reset convention shared by Marten, Polecat and Fisher, reached without a reference to any
/// of them. Internal so the lookup is unit-testable against a plain fake; see
/// <see cref="EventStores.ResetAllDataAsync"/>.
/// </summary>
internal static class StoreResetConvention
{
    private static readonly string[] resetMethodNames = ["ResetAllData", "ResetAllDataAsync"];

    public static async Task ResetAsync(object store, CancellationToken token)
    {
        var advanced = store.GetType().GetProperty("Advanced", BindingFlags.Public | BindingFlags.Instance)?.GetValue(store);
        if (advanced != null)
        {
            var reset = resetMethodNames
                .Select(name => advanced.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, [typeof(CancellationToken)]))
                .FirstOrDefault(m => m != null && typeof(Task).IsAssignableFrom(m.ReturnType));

            if (reset != null)
            {
                await ((Task)reset.Invoke(advanced, [token])!).ConfigureAwait(false);
                return;
            }

            var cleaner = advanced.GetType().GetProperty("Clean", BindingFlags.Public | BindingFlags.Instance)?.GetValue(advanced);
            if (cleaner != null && tryFind(cleaner, "DeleteAllEventDataAsync", out var events) && tryFind(cleaner, "DeleteAllDocumentsAsync", out var documents))
            {
                await events(token).ConfigureAwait(false);
                await documents(token).ConfigureAwait(false);
                return;
            }
        }

        throw new InvalidOperationException(
            $"Bobcat does not know how to reset event store type {store.GetType().FullName}. It looked for " +
            "Advanced.ResetAllData(CancellationToken) / Advanced.ResetAllDataAsync(CancellationToken), then for " +
            "Advanced.Clean.DeleteAllEventDataAsync() and DeleteAllDocumentsAsync() — the convention Marten, Polecat " +
            "and Fisher share. Reset this store in the host resource's reset hook instead.");
    }

    private static bool tryFind(object target, string name, out Func<CancellationToken, Task> call)
    {
        var withToken = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, [typeof(CancellationToken)]);
        if (withToken != null && typeof(Task).IsAssignableFrom(withToken.ReturnType))
        {
            call = token => (Task)withToken.Invoke(target, [token])!;
            return true;
        }

        var bare = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (bare != null && typeof(Task).IsAssignableFrom(bare.ReturnType))
        {
            call = _ => (Task)bare.Invoke(target, [])!;
            return true;
        }

        call = null!;
        return false;
    }
}
