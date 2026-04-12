using Alba;
using Bobcat;
using Bobcat.Runtime;
using ProjectManagement.Api;
using Shouldly;

namespace ProjectManagement.Tests;

[FixtureTitle("Project Management")]
public class ProjectManagementFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _lastStatusCode = 0;
        return Task.CompletedTask;
    }

    [When("I create a project titled {string} with admin {string}")]
    public async Task CreateProject(string title, string adminEmail)
    {
        var command = new CreateProject(title, adminEmail,
            ["tom@jasperfx.net", "bill@jasperfx.net"]);

        var result = await _host.Scenario(x =>
        {
            x.Post.Json(command).ToUrl("/api/project/create");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected) => _lastStatusCode.ShouldBe(expected);
}
