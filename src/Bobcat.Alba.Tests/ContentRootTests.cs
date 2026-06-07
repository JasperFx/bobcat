using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Alba.Tests;

public class ContentRootTests
{
    [Fact]
    public void with_content_root_is_fluent()
    {
        var resource = new AlbaResource<TestApp>();
        resource.WithContentRoot("/tmp/host").ShouldBeSameAs(resource);
    }

    [Fact]
    public void directory_not_found_is_wrapped_with_actionable_guidance()
    {
        var wrapped = AlbaResourceDiagnostics.WrapStartException(
            new DirectoryNotFoundException("doubled path"), "MySample");

        var config = wrapped.ShouldBeOfType<BobcatConfigurationException>();
        config.Message.ShouldContain("WebApplicationFactoryContentRoot");
        config.Message.ShouldContain("WithContentRoot");
        config.Message.ShouldContain("MySample");
        config.InnerException.ShouldBeOfType<DirectoryNotFoundException>();
    }

    [Fact]
    public void unrelated_exceptions_pass_through_unchanged()
    {
        var original = new InvalidOperationException("boom");
        AlbaResourceDiagnostics.WrapStartException(original, "MySample").ShouldBeSameAs(original);
    }
}
