using Bobcat.Engine;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Bobcat.Marten;

/// <summary>
/// Persistence recipe for a <c>[TableGrammar]</c>: auto-supplies the Before/After envelope and
/// a per-row sink that <c>Store</c>s each row's product, flushed with a single
/// <c>SaveChangesAsync</c> when the envelope closes.
///
/// <para>Use the generic form — <c>[MartenEntities&lt;Customer&gt;]</c> — to bind columns straight
/// to the entity with no <c>Row</c> body at all. Use the non-generic form when the grammar has a
/// hand-written <c>Row</c> that returns the entity to persist.</para>
///
/// <para>The session comes from <c>IHostResource.CurrentServices</c>, so it is the SAME
/// <see cref="IDocumentSession"/> a step gets from <c>[FromScopedService]</c> — which is what
/// makes the batched save-once behavior actually batch.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class MartenEntitiesAttribute : GrammarBehaviorAttribute
{
    /// <summary>Optional resource name, when more than one host resource is registered.</summary>
    public string? Resource { get; set; }

    public override IGrammarBehavior Build() => new MartenStorageBehavior(Resource);
}

/// <summary>
/// <see cref="MartenEntitiesAttribute"/> that also names the entity type, so columns bind to
/// <typeparamref name="T"/>'s constructor or settable properties and no <c>Row</c> is needed.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class MartenEntitiesAttribute<T> : MartenEntitiesAttribute where T : notnull
{
    public override Type? EntityType => typeof(T);
}

/// <summary>
/// The runtime half of <see cref="MartenEntitiesAttribute"/>. Lives here, in the extension
/// package, because the netstandard2.0 source generator must never reference Marten.
/// </summary>
public class MartenStorageBehavior : IGrammarBehavior
{
    private readonly string? _resourceName;
    private IDocumentSession? _session;

    public MartenStorageBehavior(string? resourceName = null) => _resourceName = resourceName;

    /// <summary>The session this envelope is writing through. Null until <see cref="Open"/>.</summary>
    public IDocumentSession? Session => _session;

    public ValueTask Open(IStepContext context)
    {
        _session = context.GetResource<IHostResource>(_resourceName)
            .CurrentServices.GetRequiredService<IDocumentSession>();

        return default;
    }

    public ValueTask Row(object? product)
    {
        if (product == null)
            throw new BobcatConfigurationException(
                "A [MartenEntities] row produced null. Row must return the entity to persist.");

        // StoreObjects, not Store<T> — the product arrives as object, and Store<object> would
        // register it under the wrong document type.
        RequireSession().StoreObjects(new[] { product });
        return default;
    }

    public async ValueTask Close()
    {
        if (_session != null)
            await _session.SaveChangesAsync();
    }

    /// <summary>
    /// The scenario scope owns the session, so the behavior deliberately does NOT dispose it —
    /// disposing here would kill the same instance the rest of the scenario's steps are using.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _session = null;
        return default;
    }

    private IDocumentSession RequireSession() => _session
        ?? throw new InvalidOperationException("The Marten storage behavior has not been opened.");
}
