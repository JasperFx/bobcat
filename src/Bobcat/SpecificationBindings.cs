using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Events.EventModeling;

namespace Bobcat;

/// <summary>
/// Points at the class Bobcat.Generators wrote into a spec assembly (bobcat#449) before JasperFx had an
/// attribute of its own. Bobcat.Generators now writes JasperFx's
/// <see cref="JasperFx.Events.EventModeling.SpecificationManifestAttribute" /> (bobcat#528), whose reader
/// still recognizes this one by name, so assemblies built with an older Bobcat keep working.
/// </summary>
[Obsolete("Bobcat.Generators writes JasperFx.Events.EventModeling.SpecificationManifestAttribute instead (bobcat#528); "
          + "read a manifest with JasperFx.Events.EventModeling.SpecificationManifest.In. This attribute will be removed.")]
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
    /// <summary>
    /// The bindings Bobcat.Generators wrote into <paramref name="assembly" />; empty when it wrote none.
    /// Forwards to JasperFx's <see cref="SpecificationManifest.In" /> (bobcat#528), which reads the manifest
    /// through either JasperFx's attribute or Bobcat's older one.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The manifest type is rooted by SpecificationManifestAttribute's DynamicallyAccessedMembers annotation.")]
    public static IReadOnlyList<SpecificationBindingDescriptor> In(Assembly assembly) => SpecificationManifest.In(assembly);
}
