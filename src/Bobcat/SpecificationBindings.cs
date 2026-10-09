using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Events.EventModeling;

namespace Bobcat;

/// <summary>
/// Points at the class Bobcat.Generators writes into a spec assembly (bobcat#449): its static
/// <c>All</c> lists every <c>[BobcatFeature]</c> test as a JasperFx <see cref="SpecificationBindingDescriptor" />.
/// Generated; never written by hand.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SpecificationManifestAttribute(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type manifestType) : Attribute
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type ManifestType { get; } = manifestType;
}

/// <summary>
/// The specifications in an assembly and the command or slice each exercises (bobcat#449), as the
/// JasperFx contract <c>EventModelSpecifications.Link</c> joins onto an assembled Event Model
/// (jasperfx#995), so a definition needs no <c>LinksToSpecification</c>.
/// </summary>
public static class SpecificationBindings
{
    /// <summary>The bindings Bobcat.Generators wrote into <paramref name="assembly" />; empty when it wrote none.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The manifest type is rooted by SpecificationManifestAttribute's DynamicallyAccessedMembers annotation.")]
    public static IReadOnlyList<SpecificationBindingDescriptor> In(Assembly assembly)
    {
        var manifest = assembly.GetCustomAttribute<SpecificationManifestAttribute>();
        return manifest?.ManifestType.GetProperty("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as IReadOnlyList<SpecificationBindingDescriptor> ?? [];
    }
}
