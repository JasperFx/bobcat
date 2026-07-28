using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>
/// The runtime half of a persistence recipe. A recipe attribute on a <c>[TableGrammar]</c> class
/// resolves to one of these, and the generated envelope calls it around the convention
/// <c>Row</c>: <see cref="Open"/> once, <see cref="Row"/> per table row with that row's product,
/// then <see cref="Close"/> once in a <c>finally</c>.
///
/// <para>The seam exists so the netstandard2.0 source generator never has to reference Marten or
/// EF Core. The generator only recognizes "this attribute is a grammar behavior" and emits a
/// generic envelope call; the behavior itself lives in the extension package, references its own
/// APIs, and resolves its session/context from <c>IHostResource.CurrentServices</c> — so a
/// recipe's session and a hand-injected <c>[FromScopedService] IDocumentSession</c> are the same
/// instance, which is what makes batched save-once work.</para>
/// </summary>
public interface IGrammarBehavior : IAsyncDisposable
{
    /// <summary>Open the envelope — resolve the session/context from the scenario scope.</summary>
    ValueTask Open(IStepContext context);

    /// <summary>Persist one row's product. Called once per table row, in order.</summary>
    ValueTask Row(object? product);

    /// <summary>Close the envelope — flush the batch (a single SaveChangesAsync).</summary>
    ValueTask Close();
}

/// <summary>
/// Base class for recipe attributes such as <c>[MartenEntities]</c> and <c>[EfCoreEntities]</c>.
/// Deriving from this is the ONLY signal the source generator needs — it never learns what the
/// recipe actually does.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public abstract class GrammarBehaviorAttribute : Attribute
{
    /// <summary>
    /// The entity type columns are bound to when the grammar has no hand-written <c>Row</c>.
    /// Null means "the entity type comes from <c>Row</c>'s return type".
    /// </summary>
    public virtual Type? EntityType => null;

    /// <summary>Create the runtime behavior for one execution of the grammar.</summary>
    public abstract IGrammarBehavior Build();
}

/// <summary>
/// Resolves the recipe attribute on a grammar class to its runtime behavior. This one lookup is
/// the accepted, bounded softening of Bobcat's no-reflection rule — only the envelope is
/// runtime-resolved; the <c>Row</c> call and the entity construction stay direct compiled code.
/// </summary>
public static class GrammarBehaviors
{
    public static IGrammarBehavior Resolve(Type grammarType)
    {
        var recipes = grammarType
            .GetCustomAttributes(typeof(GrammarBehaviorAttribute), inherit: true)
            .Cast<GrammarBehaviorAttribute>()
            .ToArray();

        return recipes.Length switch
        {
            0 => throw new BobcatConfigurationException(
                $"Table grammar '{grammarType.Name}' has no persistence recipe attribute."),
            1 => recipes[0].Build(),
            _ => throw new BobcatConfigurationException(
                $"Table grammar '{grammarType.Name}' has more than one persistence recipe attribute " +
                $"({string.Join(", ", recipes.Select(r => r.GetType().Name))}). Apply exactly one.")
        };
    }
}
