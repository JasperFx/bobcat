using Bobcat.Runtime;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Bobcat.Alba.Tests;

public class AlbaResourceTests
{
    private class FakeProgram { }

    [Fact]
    public void implements_ITestResource()
    {
        var resource = new AlbaResource<FakeProgram>();
        resource.ShouldBeAssignableTo<ITestResource>();
    }

    [Fact]
    public void name_defaults_to_program_type_name()
    {
        var resource = new AlbaResource<FakeProgram>();
        resource.Name.ShouldBe(nameof(FakeProgram));
    }

    [Fact]
    public void name_can_be_overridden()
    {
        var resource = new AlbaResource<FakeProgram>("my-app");
        resource.Name.ShouldBe("my-app");
    }

    [Fact]
    public void last_result_is_null_initially()
    {
        var resource = new AlbaResource<FakeProgram>();
        resource.LastResult.ShouldBeNull();
    }

    [Fact]
    public async Task reset_between_scenarios_clears_last_result()
    {
        var resource = new AlbaResource<FakeProgram>();
        resource.LastResult = Substitute.For<global::Alba.IScenarioResult>();
        resource.LastResult.ShouldNotBeNull();

        await resource.ResetBetweenScenarios();

        resource.LastResult.ShouldBeNull();
    }
}
