using System.Reflection;
using Bobcat.Runtime;

namespace Bobcat.CodeFirst;

/// <summary>
/// Registers code-first <see cref="Specification"/>s with a <see cref="BobcatRunner"/>, beside
/// whatever generated features <see cref="BobcatRunner.ScanForFeatures"/> found. The two kinds
/// of feature are indistinguishable to the runner from here on.
/// </summary>
public static class SpecificationRunnerExtensions
{
    /// <summary>Register one specification type.</summary>
    public static BobcatRunner AddSpecification<T>(this BobcatRunner runner) where T : Specification, new()
        => runner.AddFeature(SpecificationFeature.Build<T>());

    /// <summary>Register one specification type.</summary>
    public static BobcatRunner AddSpecification(this BobcatRunner runner, Type specificationType)
        => runner.AddFeature(SpecificationFeature.Build(specificationType));

    /// <summary>
    /// Register every concrete <see cref="Specification"/> in <paramref name="assembly"/>. Pair it
    /// with <see cref="BobcatRunner.ScanForFeatures"/> in a project that mixes the two styles.
    /// </summary>
    public static BobcatRunner ScanForSpecifications(this BobcatRunner runner, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || !typeof(Specification).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) == null) continue;

            runner.AddSpecification(type);
        }

        return runner;
    }
}
