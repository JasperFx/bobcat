using System.Reflection;
using System.Runtime.CompilerServices;

namespace Bobcat.Monitoring;

/// <summary>
/// Where a spec assembly hands core its half of the Event Model, as JSON (bobcat#744). The generator
/// emits one <see cref="Register"/> call per spec assembly, from a module initializer, beside its
/// <c>BobcatEventModelSource</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a bridge.</b> Core publishes the spec half to a console (<see cref="SpecEventModelPublisher"/>,
/// #294), but since #744 it references no JasperFx, so it cannot load the generated
/// <c>IEventModelDefinitionSource</c> or serialize its descriptor. The spec assembly can: the generator
/// emits the source only where the consumer's compilation has JasperFx.Events. So the generated code does the
/// JasperFx half (describe, stamp provenance, serialize) behind a <see cref="Func{TResult}"/>, and core
/// handles only the model name, the source name and a string.
/// </para>
/// <para>
/// <b>The JSON is produced on demand, not at registration.</b> A module initializer runs whenever the
/// assembly loads, including in runs that never attach to a console; describing the model there would charge
/// every one of them for a document nobody asked for.
/// </para>
/// </remarks>
public static class SpecEventModels
{
    private static readonly List<Registration> registrations = new();
    private static readonly object gate = new();

    /// <summary>One spec assembly's half: the model it names, the assembly it came from, and its JSON.</summary>
    /// <param name="ModelName">The descriptor's name — <c>[assembly: EventModelName]</c>, else the assembly name.</param>
    /// <param name="Source">The spec assembly's name; the publisher sanitizes it into a source name.</param>
    /// <param name="Json">The provenance-stamped descriptor, serialized for the console's wire.</param>
    public sealed record Registration(string ModelName, string Source, Func<string> Json);

    /// <summary>
    /// Register a spec assembly's half. Called from generated code; registering the same source twice keeps
    /// the last one.
    /// </summary>
    public static void Register(string modelName, string source, Func<string> json)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(json);

        lock (gate)
        {
            registrations.RemoveAll(x => x.Source == source);
            registrations.Add(new Registration(modelName, source, json));
        }
    }

    /// <summary>Every half registered so far.</summary>
    public static IReadOnlyList<Registration> All
    {
        get
        {
            lock (gate) return registrations.ToList();
        }
    }

    /// <summary>
    /// The half <paramref name="assembly"/> registered, or null when it has none. Runs the assembly's module
    /// initializer first: a session hook can open a run before any code in the spec assembly has executed,
    /// and loading an assembly does not by itself run its initializer.
    /// </summary>
    public static Registration? For(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (name is null) return null;

        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);

        lock (gate) return registrations.LastOrDefault(x => x.Source == name);
    }

    /// <summary>Test seam: forget every registration.</summary>
    internal static void Clear()
    {
        lock (gate) registrations.Clear();
    }
}
