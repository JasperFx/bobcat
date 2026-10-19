using Shouldly;

namespace Bobcat.Tests;

/// <summary>
/// bobcat#744: Bobcat core references no JasperFx and no Wolverine assembly. A test framework must not pin
/// its users' application framework, and a JasperFx reference in core was a JasperFx floor in every consumer's
/// restore (NU1605, NU1109, low pins resolving silently in #740). The Critter Stack vocabulary that needed it
/// ships in WolverineFx.Bobcat; the Event Model reaches core as JSON through the generated bridge.
/// </summary>
public class BobcatAssemblyReferenceTests
{
    [Fact]
    public void core_references_no_jasperfx_or_wolverine_assembly()
    {
        var offending = typeof(ScenarioRecorder).Assembly.GetReferencedAssemblies()
            .Select(x => x.Name ?? "")
            .Where(x => x.StartsWith("JasperFx", StringComparison.OrdinalIgnoreCase)
                        || x.StartsWith("Wolverine", StringComparison.OrdinalIgnoreCase))
            .ToList();

        offending.ShouldBeEmpty();
    }
}
