using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#745: Bobcat.Supervisor references no JasperFx and no Wolverine assembly, the same rule
/// <c>BobcatAssemblyReferenceTests</c> holds core to (#744). A build tool that drives a suite through the
/// supervisor must not inherit a JasperFx floor from it (#740's NU1605/NU1109 and silent low pins).
/// </summary>
public class SupervisorAssemblyReferenceTests
{
    [Fact]
    public void the_supervisor_references_no_jasperfx_or_wolverine_assembly()
    {
        var offending = typeof(Supervisor).Assembly.GetReferencedAssemblies()
            .Select(x => x.Name ?? "")
            .Where(x => x.StartsWith("JasperFx", StringComparison.OrdinalIgnoreCase)
                        || x.StartsWith("Wolverine", StringComparison.OrdinalIgnoreCase))
            .ToList();

        offending.ShouldBeEmpty();
    }
}
