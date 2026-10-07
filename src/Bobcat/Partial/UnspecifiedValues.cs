using Bobcat.Engine;
using Bobcat.Partial;

namespace Bobcat;

/// <summary>A member a partial object did not specify, which the build still has to give a value.</summary>
/// <param name="DeclaringType">The type being built.</param>
/// <param name="Path">The member's path from the root object, such as <c>Address.City</c>.</param>
/// <param name="Type">The member's type.</param>
/// <param name="AllowsNull">
/// Whether null is a legal value: a <see cref="Nullable{T}"/>, or a reference type annotated as
/// nullable. An unannotated reference type (an F# type, or code without nullable annotations) is
/// treated as not allowing it.
/// </param>
public sealed record UnspecifiedMember(Type DeclaringType, string Path, Type Type, bool AllowsNull);

/// <summary>
/// What fills the members a partial object does not specify (bobcat#417). The policy itself is
/// still a design question (bobcat#421: Bogus/AutoBogus, Storyteller-style declared defaults); this
/// seam lets that land without touching how objects are built.
/// </summary>
public interface IUnspecifiedValues
{
    /// <summary>
    /// Supply a value for <paramref name="member"/>, or return false to leave it to the builder,
    /// which then uses <c>default</c> for a value type and builds a reference type with nothing
    /// specified (or leaves it null when it cannot be built).
    /// </summary>
    bool TryValueFor(UnspecifiedMember member, out object? value);
}

/// <summary>
/// The default policy: predictable values that respect nullability, so an unspecified member is
/// never a surprise null and two runs build the same object apart from fresh identifiers.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>null wherever null is allowed;</item>
/// <item><c>""</c> for a string, an empty collection for a collection, <c>None</c> for an F# option;</item>
/// <item>a fresh <see cref="Guid"/>, because <see cref="Guid.Empty"/> as an id makes two arranged
/// objects the same identity;</item>
/// <item>the Bobcat clock's "now" for a date or time, so a controlled clock controls these too;</item>
/// <item>an enum's first declared value, and <c>default</c> for every other value type.</item>
/// </list>
/// A reference type outside these is left to the builder, which builds it with nothing specified.
/// </remarks>
public sealed class PredictableValues : IUnspecifiedValues
{
    public static PredictableValues Instance { get; } = new();

    public bool TryValueFor(UnspecifiedMember member, out object? value)
    {
        var type = member.Type;
        value = null;

        if (member.AllowsNull) return true;

        if (FSharpShapes.IsOption(type) || FSharpShapes.IsValueOption(type))
        {
            value = FSharpShapes.None(type);
            return true;
        }

        if (type == typeof(string))
        {
            value = "";
            return true;
        }

        if (type == typeof(Guid))
        {
            value = Guid.NewGuid();
            return true;
        }

        var now = BobcatClock.Current.GetUtcNow();
        if (type == typeof(DateTimeOffset)) { value = now; return true; }
        if (type == typeof(DateTime)) { value = now.UtcDateTime; return true; }
        if (type == typeof(DateOnly)) { value = DateOnly.FromDateTime(now.UtcDateTime); return true; }
        if (type == typeof(TimeOnly)) { value = TimeOnly.FromDateTime(now.UtcDateTime); return true; }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            value = values.Length > 0 ? values.GetValue(0) : Activator.CreateInstance(type);
            return true;
        }

        if (type.IsValueType)
        {
            value = Activator.CreateInstance(type);
            return true;
        }

        var empty = CollectionShapes.Empty(type);
        if (empty is not null)
        {
            value = empty;
            return true;
        }

        return false;
    }
}

/// <summary>
/// <c>default(T)</c> for every unspecified member — the rule a Gherkin arrange has had since
/// issue #241, kept for <c>RecordBuilding</c> until bobcat#421 settles the policy, so the Gherkin
/// lane's behaviour changes once rather than twice.
/// </summary>
public sealed class DefaultValues : IUnspecifiedValues
{
    public static DefaultValues Instance { get; } = new();

    public bool TryValueFor(UnspecifiedMember member, out object? value)
    {
        value = member.Type.IsValueType ? Activator.CreateInstance(member.Type) : null;
        return true;
    }
}
