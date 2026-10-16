using System.Reflection;

namespace Bobcat.Runtime;

/// <summary>
/// The traits a projected test declares through its own framework's attributes (bobcat#573), read in
/// the suite's process when it writes its manifest, so <c>bobcat run --trait</c> can select on them
/// without loading the assembly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read by shape, never by reference.</b> Core names no test framework, so an attribute is read
/// for what it looks like: one with a public parameterless <c>GetTraits()</c> returning key/value
/// pairs (xUnit's <c>ITraitAttribute</c>, so a project's own trait attributes count too); one named
/// <c>PropertyAttribute</c> or <c>TestPropertyAttribute</c> with string <c>Name</c> and
/// <c>Value</c> (TUnit, NUnit, MSTest); and <c>CategoryAttribute</c> / <c>TestCategoryAttribute</c>,
/// read as a <c>Category</c> trait (NUnit, MSTest — TUnit's <c>[Category]</c> is a property too).
/// </para>
/// <para>
/// Bobcat's own attributes are skipped: what they project is the coordinates, which the entry already
/// carries in its own fields.
/// </para>
/// </remarks>
public static class SpecTraits
{
    public const string CategoryTrait = "Category";

    // The packages whose attributes project coordinates; a project's own assembly may well be named Bobcat.*
    private static readonly HashSet<string> ownAssemblies =
        new(["Bobcat", "Bobcat.Xunit", "Bobcat.TUnit", "Bobcat.NUnit", "Bobcat.MSTest"], StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="entry"/> with <see cref="SpecManifestEntry.Traits"/> filled from its test
    /// method, class and assembly. Unchanged when the test cannot be found or declares nothing.
    /// </summary>
    public static SpecManifestEntry WithDeclaredTraits(SpecManifestEntry entry, Assembly assembly)
    {
        try
        {
            if (entry.TestClass is not { Length: > 0 } className || entry.TestMethod is not { Length: > 0 } methodName)
                return entry;

            var type = assembly.GetType(className);
            if (type is null) return entry;

            var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(x => x.Name == methodName);

            var traits = Read(assembly.GetCustomAttributes()
                .Concat(type.GetCustomAttributes(inherit: true).OfType<Attribute>())
                .Concat(method?.GetCustomAttributes(inherit: true).OfType<Attribute>() ?? []));

            return traits.Count == 0 ? entry : entry with { Traits = traits };
        }
        catch
        {
            // A listing must never fail the suite (SpecManifest.WriteIfRequested); no traits is the honest answer
            return entry;
        }
    }

    /// <summary>The traits <paramref name="attributes"/> declare, by name, each value once, in order.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Read(IEnumerable<Attribute> attributes)
    {
        var traits = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var attribute in attributes)
        {
            var type = attribute.GetType();
            if (type.Assembly.GetName().Name is { } owner && ownAssemblies.Contains(owner)) continue;

            foreach (var (name, value) in pairsOf(attribute, type))
            {
                if (string.IsNullOrEmpty(name) || value is null) continue;

                if (!traits.TryGetValue(name, out var values)) traits[name] = values = [];
                if (!values.Contains(value)) values.Add(value);
            }
        }

        return traits.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value, StringComparer.Ordinal);
    }

    private static IEnumerable<(string? Name, string? Value)> pairsOf(Attribute attribute, Type type)
    {
        var getTraits = type.GetMethod("GetTraits", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (getTraits?.Invoke(attribute, null) is IEnumerable<KeyValuePair<string, string>> declared)
        {
            foreach (var pair in declared) yield return (pair.Key, pair.Value);
            yield break;
        }

        switch (type.Name)
        {
            case "PropertyAttribute" or "TestPropertyAttribute":
                yield return (stringOf(attribute, type, "Name"), stringOf(attribute, type, "Value"));
                break;

            case "CategoryAttribute" when stringOf(attribute, type, "Name") is { } category:
                yield return (CategoryTrait, category);
                break;

            case "TestCategoryAttribute" when type.GetProperty("TestCategories")?.GetValue(attribute) is IEnumerable<string> categories:
                foreach (var category in categories) yield return (CategoryTrait, category);
                break;
        }
    }

    private static string? stringOf(Attribute attribute, Type type, string property)
        => type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(attribute) as string;
}
