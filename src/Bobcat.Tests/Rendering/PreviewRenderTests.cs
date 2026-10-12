using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Rendering;

/// <summary>
/// Issue #208: a preview step's binding, as the preview renders it.
/// </summary>
public class PreviewRenderTests
{
    [Fact]
    public void binding_type_names_render_without_namespace_qualification()
    {
        new StepBinding("global::My.App.OrderFixture", "Place", "an order is placed", [])
            .DeclaringTypeName.ShouldBe("OrderFixture");
        new StepBinding("OrderFixture", "Place", "an order is placed", [])
            .DeclaringTypeName.ShouldBe("OrderFixture");
    }
}
