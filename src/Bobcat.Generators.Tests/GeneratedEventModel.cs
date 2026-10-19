using System.Reflection;
using JasperFx.Events.EventModeling;

namespace Bobcat.Generators.Tests;

/// <summary>
/// The Event Model half a generated spec assembly declares, read back for an assertion: the generated
/// <c>BobcatEventModelSource</c>'s descriptor, provenance-stamped the way <c>EventModelDiscovery</c> stamps it.
/// </summary>
/// <remarks>
/// A test-local copy of what core's <c>GeneratedEventModel</c> did until bobcat#744 moved it, with
/// <c>SpecIdentityAudit</c>, to WolverineFx.Bobcat: core no longer references JasperFx, and these tests compile
/// generated code against the real JasperFx.Events.
/// </remarks>
internal static class GeneratedEventModel
{
    public const string GeneratedSourceTypeName = "Bobcat.Generated.EventModel.BobcatEventModelSource";

    public static EventModelDescriptor? For(Assembly assembly)
    {
        var type = assembly.GetType(GeneratedSourceTypeName, throwOnError: false);
        if (type is null) return null;

        var instance = type
            .GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null) as IEventModelDefinitionSource;

        instance ??= Activator.CreateInstance(type, nonPublic: true) as IEventModelDefinitionSource;
        if (instance is null) return null;

        var descriptor = instance
            .TryCreateAsync(EmptyServiceProvider.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        return descriptor?.WithProvenance(instance.Provenance);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
