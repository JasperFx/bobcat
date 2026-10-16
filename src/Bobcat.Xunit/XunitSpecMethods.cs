using System.Collections.Concurrent;
using System.Reflection;

namespace Bobcat.Xunit;

/// <summary>
/// Which test method a <see cref="BobcatSpecAttribute"/> is on, found by the source location the
/// compiler stamped on it (bobcat#573).
/// </summary>
/// <remarks>
/// <para>
/// xUnit v3's <c>ITraitAttribute.GetTraits()</c> takes no argument, so the attribute instance does not
/// know its method. Its <c>[CallerFilePath]</c>/<c>[CallerLineNumber]</c> pair does identify it, and
/// reading the same pair off every <see cref="BobcatSpecAttribute"/> in the loaded test assemblies
/// gives the method back — exactly, with no path mapping to undo, because both sides are the value
/// the compiler wrote.
/// </para>
/// <para>
/// Indexed once per assembly and kept: assemblies only ever arrive, and a location the index lacks
/// re-scans whatever has loaded since.
/// </para>
/// </remarks>
internal static class XunitSpecMethods
{
    private static readonly ConcurrentDictionary<(string File, int Line), MethodInfo> _byLocation = new();
    private static readonly ConcurrentDictionary<Assembly, bool> _indexed = new();

    public static MethodInfo? Find(string? sourceFilePath, int? sourceLineNumber)
    {
        if (string.IsNullOrEmpty(sourceFilePath) || sourceLineNumber is not { } line) return null;

        var key = (sourceFilePath, line);
        if (_byLocation.TryGetValue(key, out var method)) return method;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (_indexed.TryAdd(assembly, true)) index(assembly);
        }

        return _byLocation.TryGetValue(key, out method) ? method : null;
    }

    private static void index(Assembly assembly)
    {
        if (assembly.IsDynamic || !referencesThisAdapter(assembly)) return;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.OfType<Type>().ToArray();
        }

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                 | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(all))
            {
                BobcatSpecAttribute? spec;
                try
                {
                    spec = method.GetCustomAttribute<BobcatSpecAttribute>(inherit: true);
                }
                catch
                {
                    continue;
                }

                if (spec?.SourceFilePath is { Length: > 0 } file && spec.SourceLineNumber is { } line)
                {
                    _byLocation.TryAdd((file, line), method);
                }
            }
        }
    }

    private static bool referencesThisAdapter(Assembly assembly)
    {
        var self = typeof(XunitSpecMethods).Assembly.GetName().Name;
        try
        {
            return assembly.GetReferencedAssemblies().Any(x => x.Name == self);
        }
        catch
        {
            return false;
        }
    }
}
