using Bobcat;
using Bobcat.Runtime;

namespace CqrsMinimalApi.Tests;

/// <summary>
/// Suite configuration for this spec project. No <c>Main</c>: the project references
/// <c>Bobcat.Mtp</c> and declares no entry point, so the generator emits the
/// Microsoft.Testing.Platform one and calls every <c>[BobcatConfiguration]</c> method.
/// Declaring none is also what keeps <c>WebApp</c>'s unqualified <c>Program</c> unambiguous —
/// see docs/resources.md.
/// </summary>
public static class SpecsRunner
{
    [BobcatConfiguration]
    public static void Configure(BobcatRunner runner) => runner.Resources.Add(new WebApp());
}
