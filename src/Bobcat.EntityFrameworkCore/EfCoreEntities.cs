using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bobcat.EntityFrameworkCore;

/// <summary>
/// Persistence recipe for a <c>[TableGrammar]</c>: auto-supplies the Before/After envelope and a
/// per-row sink that <c>Add</c>s each row's product to the <see cref="DbContext"/>, flushed with
/// a single <c>SaveChangesAsync</c> when the envelope closes.
///
/// <para>Use the generic form — <c>[EfCoreEntities&lt;Customer&gt;]</c> — to bind columns straight
/// to the entity with no <c>Row</c> body at all. Use the non-generic form when the grammar has a
/// hand-written <c>Row</c> that returns the entity to persist.</para>
///
/// <para>The context comes from <c>IHostResource.CurrentServices</c>, so it is the SAME instance
/// a step gets from <c>[FromScopedService]</c> — the change tracker is shared, which is what
/// makes the batched save-once behavior actually batch.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class EfCoreEntitiesAttribute : GrammarBehaviorAttribute
{
    /// <summary>
    /// The <see cref="DbContext"/> type to resolve. Defaults to <see cref="DbContext"/> itself,
    /// which works when the context is registered under that base type; name your own type when
    /// <c>AddDbContext&lt;TContext&gt;</c> only registers the concrete one.
    /// </summary>
    public Type ContextType { get; set; } = typeof(DbContext);

    /// <summary>Optional resource name, when more than one host resource is registered.</summary>
    public string? Resource { get; set; }

    public override IGrammarBehavior Build() => new EfCoreStorageBehavior(ContextType, Resource);
}

/// <summary>
/// <see cref="EfCoreEntitiesAttribute"/> that also names the entity type, so columns bind to
/// <typeparamref name="T"/>'s constructor or settable properties and no <c>Row</c> is needed.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class EfCoreEntitiesAttribute<T> : EfCoreEntitiesAttribute where T : class
{
    public override Type? EntityType => typeof(T);
}

/// <summary>
/// The runtime half of <see cref="EfCoreEntitiesAttribute"/>. Lives here, in the extension
/// package, because the netstandard2.0 source generator must never reference EF Core.
/// </summary>
public class EfCoreStorageBehavior : IGrammarBehavior
{
    private readonly Type _contextType;
    private readonly string? _resourceName;
    private DbContext? _context;

    public EfCoreStorageBehavior(Type? contextType = null, string? resourceName = null)
    {
        _contextType = contextType ?? typeof(DbContext);
        _resourceName = resourceName;
    }

    /// <summary>The context this envelope is writing through. Null until <see cref="Open"/>.</summary>
    public DbContext? Context => _context;

    public ValueTask Open(IStepContext context)
    {
        var services = context.GetResource<IHostResource>(_resourceName).CurrentServices;

        _context = services.GetRequiredService(_contextType) as DbContext
            ?? throw new BobcatConfigurationException(
                $"Service '{_contextType.Name}' is registered but is not a DbContext.");

        return default;
    }

    public ValueTask Row(object? product)
    {
        if (product == null)
            throw new BobcatConfigurationException(
                "An [EfCoreEntities] row produced null. Row must return the entity to persist.");

        requireContext().Add(product);
        return default;
    }

    public async ValueTask Close()
    {
        if (_context != null)
            await _context.SaveChangesAsync();
    }

    /// <summary>
    /// The scenario scope owns the DbContext, so the behavior deliberately does NOT dispose it —
    /// disposing here would kill the same instance the rest of the scenario's steps are using.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _context = null;
        return default;
    }

    private DbContext requireContext() => _context
        ?? throw new InvalidOperationException("The EF Core storage behavior has not been opened.");
}
