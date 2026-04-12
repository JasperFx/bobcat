using Bobcat;
using Bobcat.Alba;

namespace ProjectManagement.Tests;

[FixtureTitle("Project Management")]
public class ProjectManagementFixture
{
    private Guid _projectId;
    private int _lastStatusCode;

    [When("I create a project named {string} with description {string}")]
    public async Task CreateProject(IStepContext context, string name, string description)
    {
        var result = await context.PostJsonAsync<CreateProjectRequest, CreateProjectResponse>(
            "/api/projects",
            new CreateProjectRequest(name, description));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _projectId = result.Body.ProjectId;
    }

    [Then("the response status is {int}")]
    [Check]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Then("the project id is returned")]
    [Check]
    public bool ProjectIdReturned() => _projectId != Guid.Empty;
}

record CreateProjectRequest(string Name, string Description);
record CreateProjectResponse(Guid ProjectId);
