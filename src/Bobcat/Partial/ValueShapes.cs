using System.Collections;
using System.Reflection;

namespace Bobcat.Partial;

/// <summary>
/// The F# shapes a partial object has to build — options, lists, sets, maps and single-case unions —
/// recognized by name, so Bobcat never references FSharp.Core (bobcat#417).
/// </summary>
/// <remarks>
/// An F# record needs nothing here: it compiles to a class whose one public constructor takes every
/// field, which the ordinary constructor binding already handles. These are the shapes that have no
/// C# equivalent a reflection call would find on its own.
/// </remarks>
internal static class FSharpShapes
{
    private const string OptionName = "Microsoft.FSharp.Core.FSharpOption`1";
    private const string ValueOptionName = "Microsoft.FSharp.Core.FSharpValueOption`1";
    private const string ListName = "Microsoft.FSharp.Collections.FSharpList`1";
    private const string SetName = "Microsoft.FSharp.Collections.FSharpSet`1";
    private const string MapName = "Microsoft.FSharp.Collections.FSharpMap`2";
    private const string CompilationMappingName = "Microsoft.FSharp.Core.CompilationMappingAttribute";

    // SourceConstructFlags.SumType: the type is a union.
    private const int SumType = 1;

    public static bool IsOption(Type type) => isGeneric(type, OptionName);
    public static bool IsValueOption(Type type) => isGeneric(type, ValueOptionName);
    public static bool IsList(Type type) => isGeneric(type, ListName);
    public static bool IsSet(Type type) => isGeneric(type, SetName);
    public static bool IsMap(Type type) => isGeneric(type, MapName);

    /// <summary>The <c>T</c> of an <c>option</c> or <c>voption</c>, or null for anything else.</summary>
    public static Type? OptionValueType(Type type)
        => IsOption(type) || IsValueOption(type) ? type.GetGenericArguments()[0] : null;

    /// <summary><c>None</c>: a null reference for <c>option</c>, the default struct for <c>voption</c>.</summary>
    public static object? None(Type optionType)
        => IsValueOption(optionType)
            ? optionType.GetProperty("ValueNone", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)
            : null;

    public static object Some(Type optionType, object? value)
    {
        // option has a static Some; voption compiles its case factory as NewValueSome.
        var factory = optionType.GetMethod("Some", BindingFlags.Public | BindingFlags.Static)
                      ?? optionType.GetMethod("NewValueSome", BindingFlags.Public | BindingFlags.Static)!;
        return factory.Invoke(null, [value])!;
    }

    /// <summary>An F# list, set or map holding <paramref name="items"/> (key/value pairs for a map).</summary>
    public static object Collection(Type type, IReadOnlyList<object?> items)
    {
        if (IsList(type))
        {
            var list = type.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
            var cons = type.GetMethod("Cons", BindingFlags.Public | BindingFlags.Static)!;
            for (var i = items.Count - 1; i >= 0; i--) list = cons.Invoke(null, [items[i], list]);
            return list!;
        }

        var element = IsMap(type)
            ? typeof(Tuple<,>).MakeGenericType(type.GetGenericArguments())
            : type.GetGenericArguments()[0];

        var typed = Array.CreateInstance(element, items.Count);
        for (var i = 0; i < items.Count; i++) typed.SetValue(items[i], i);

        var sequence = typeof(IEnumerable<>).MakeGenericType(element);
        return type.GetConstructor([sequence])!.Invoke([typed]);
    }

    /// <summary>
    /// The factory of a single-case union with exactly one field — <c>type OrderId = OrderId of Guid</c>,
    /// the usual F# way to wrap an identifier — or null for any other type.
    /// </summary>
    public static MethodInfo? SingleCaseWrapper(Type type)
    {
        if (!isUnion(type)) return null;

        var factories = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("New", StringComparison.Ordinal) && m.ReturnType == type)
            .ToList();

        // A single case with fields compiles to one New<Case> factory; a union with several cases
        // has one per case, and which case a value means is not a question this can answer.
        if (factories.Count != 1) return null;
        return factories[0].GetParameters().Length == 1 ? factories[0] : null;
    }

    private static bool isUnion(Type type)
        => type.CustomAttributes.Any(a => a.AttributeType.FullName == CompilationMappingName
                                          && a.ConstructorArguments.Count > 0
                                          && a.ConstructorArguments[0].Value is int flags
                                          && (flags & 31) == SumType);

    private static bool isGeneric(Type type, string definition)
        => type.IsGenericType && type.GetGenericTypeDefinition().FullName == definition;
}

/// <summary>
/// The collection shapes a partial object fills: arrays, the BCL list/set/dictionary types and their
/// interfaces, and the F# collections — empty when unspecified, or from a value list when specified.
/// </summary>
internal static class CollectionShapes
{
    /// <summary>The element type, or null when <paramref name="type"/> is not a collection this builds.</summary>
    public static Type? ElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        if (FSharpShapes.IsList(type) || FSharpShapes.IsSet(type)) return type.GetGenericArguments()[0];
        if (!type.IsGenericType) return null;

        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(List<>) || definition == typeof(HashSet<>)
            || definition == typeof(IEnumerable<>) || definition == typeof(IList<>)
            || definition == typeof(ICollection<>) || definition == typeof(IReadOnlyList<>)
            || definition == typeof(IReadOnlyCollection<>) || definition == typeof(ISet<>)
            || definition == typeof(IReadOnlySet<>))
        {
            return type.GetGenericArguments()[0];
        }

        return null;
    }

    public static bool IsDictionary(Type type)
    {
        if (FSharpShapes.IsMap(type)) return true;
        if (!type.IsGenericType) return false;

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>)
                                                   || definition == typeof(IReadOnlyDictionary<,>);
    }

    /// <summary>An empty instance of a collection or dictionary type, or null when it is neither.</summary>
    public static object? Empty(Type type)
    {
        if (IsDictionary(type))
        {
            if (FSharpShapes.IsMap(type)) return FSharpShapes.Collection(type, []);
            return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(type.GetGenericArguments()));
        }

        return ElementType(type) is null ? null : Make(type, []);
    }

    /// <summary>A collection of <paramref name="type"/> holding <paramref name="items"/>, already converted.</summary>
    public static object Make(Type type, IReadOnlyList<object?> items)
    {
        if (FSharpShapes.IsList(type) || FSharpShapes.IsSet(type)) return FSharpShapes.Collection(type, items);

        var element = ElementType(type)!;
        if (type.IsArray)
        {
            var array = Array.CreateInstance(element, items.Count);
            for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
            return array;
        }

        var definition = type.GetGenericTypeDefinition();
        var concrete = definition == typeof(HashSet<>) || definition == typeof(ISet<>)
                                                       || definition == typeof(IReadOnlySet<>)
            ? typeof(HashSet<>).MakeGenericType(element)
            : typeof(List<>).MakeGenericType(element);

        var collection = Activator.CreateInstance(concrete)!;
        var add = concrete.GetMethod("Add")!;
        foreach (var item in items) add.Invoke(collection, [item]);
        return collection;
    }

    /// <summary>The items of any enumerable value, or null when it is not one (a string is not).</summary>
    public static IReadOnlyList<object?>? ItemsOf(object value)
        => value is string or not IEnumerable ? null : ((IEnumerable)value).Cast<object?>().ToList();
}
