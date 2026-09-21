using System.Reflection;
using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// <see cref="BobcatRunner.ScanForFeatures"/> reads an assembly through JasperFx's
/// <c>TypeRepository</c>. These pin the shape it accepts and — the part with a behaviour change in
/// it — that scanning is now public-types-only.
/// </summary>
/// <remarks>
/// Not covered here: a spec assembly that fails to LOAD raises a
/// <see cref="BobcatConfigurationException"/> naming it, rather than discovering nothing. That
/// path was verified by hand against a purpose-built pair of assemblies — compile one referencing
/// another, delete the dependency from the output directory, scan — where <c>GetTypes()</c> throws
/// <c>ReflectionTypeLoadException</c> and <c>TypeRepository</c> silently returns zero types and
/// records the assembly. Reproducing that in-process needs two throwaway projects, which is more
/// machinery than the guard is worth; it is written down here so the gap is known.
/// </remarks>
public class ScanForFeaturesTests
{
    private static BobcatRunner scanThisAssembly()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.ScanForFeatures(typeof(ScanForFeaturesTests).Assembly);
        return runner;
    }

    [Fact]
    public void finds_a_public_static_class_with_a_define_method()
        => scanThisAssembly().Features.ShouldContain(f => f.Title == "Scanned Public");

    [Fact]
    public void ignores_a_define_method_returning_the_wrong_type()
        => scanThisAssembly().Features.ShouldNotContain(f => f.Title == "Wrong Return");

    [Fact]
    public void ignores_a_non_static_class_carrying_a_define_method()
        => scanThisAssembly().Features.ShouldNotContain(f => f.Title == "Not Static");

    /// <summary>
    /// The one behaviour change in moving to JasperFx: <c>TypeRepository</c> returns exactly what
    /// <c>GetExportedTypes()</c> does, so an internal factory is no longer discovered. Every
    /// generated <c>*_Feature</c> class is public, so nothing the generator emits is affected —
    /// this pins the narrowing so it stays deliberate.
    /// </summary>
    [Fact]
    public void does_not_find_an_internal_static_class()
        => scanThisAssembly().Features.ShouldNotContain(f => f.Title == "Scanned Internal");

    [Fact]
    public void scanning_twice_is_not_a_reason_to_lose_a_feature()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.ScanForFeatures(typeof(ScanForFeaturesTests).Assembly);
        runner.ScanForFeatures(typeof(ScanForFeaturesTests).Assembly);

        // TypeRepository caches per assembly; the second pass must still see the same types.
        runner.Features.Count(f => f.Title == "Scanned Public").ShouldBe(2);
    }
}

// ── the corpus the tests above scan for ──────────────────────────────────────

public class ScannedFixture : Fixture;

public static class Scanned_Public_Feature
{
    public static FeatureDefinition Define() => new("Scanned Public", typeof(ScannedFixture), []);
}

internal static class Scanned_Internal_Feature
{
    public static FeatureDefinition Define() => new("Scanned Internal", typeof(ScannedFixture), []);
}

public static class Wrong_Return_Feature
{
    public static string Define() => "Wrong Return";
}

public class Not_Static_Feature
{
    public static FeatureDefinition Define() => new("Not Static", typeof(ScannedFixture), []);
}
