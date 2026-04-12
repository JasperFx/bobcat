using Alba;
using Bobcat.Alba.Tests.TestApp;
using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Alba.Tests;

public class AlbaResourceTests
{
    [Fact]
    public void name_is_set_from_constructor()
    {
        var resource = TestWebApp.Create();
        resource.Name.ShouldBe(TestWebApp.ResourceName);
    }

    [Fact]
    public void implements_ITestResource()
    {
        var resource = TestWebApp.Create();
        resource.ShouldBeAssignableTo<ITestResource>();
    }

    [Fact]
    public void last_result_is_null_initially()
    {
        var resource = TestWebApp.Create();
        resource.LastResult.ShouldBeNull();
    }

    [Fact]
    public async Task reset_clears_last_result()
    {
        var resource = TestWebApp.Create();
        var suite = new TestSuite();
        suite.AddResource(resource);
        await suite.StartAll();
        try
        {
            var ctx = new SpecExecutionContext("test", suite: suite);
            await ctx.ScenarioAsync(s => s.Get.Url("/api/hello"), TestWebApp.ResourceName);
            resource.LastResult.ShouldNotBeNull();

            await resource.ResetBetweenScenarios();
            resource.LastResult.ShouldBeNull();
        }
        finally
        {
            await suite.DisposeAsync();
        }
    }

    [Fact]
    public async Task start_creates_host()
    {
        var resource = TestWebApp.Create();
        await resource.Start();
        try
        {
            resource.Host.ShouldNotBeNull();
        }
        finally
        {
            await resource.DisposeAsync();
        }
    }

    [Fact]
    public async Task can_execute_scenario_through_host()
    {
        var resource = TestWebApp.Create();
        await resource.Start();
        try
        {
            var result = await resource.Host.Scenario(s =>
            {
                s.Get.Url("/api/hello");
                s.StatusCodeShouldBeOk();
            });
            var response = await result.ReadAsJsonAsync<HelloResponse>();
            response.Message.ShouldBe("hello");
        }
        finally
        {
            await resource.DisposeAsync();
        }
    }
}
