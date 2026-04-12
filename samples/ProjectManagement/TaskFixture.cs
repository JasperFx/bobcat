using System.Text.Json;
using Alba;
using Bobcat;
using Bobcat.Runtime;
using Bobcat.Wolverine;

namespace ProjectManagement;

[FixtureTitle("Project Task Management")]
public class TaskFixture : Fixture
{
    private IAlbaHost _host = null!;
    private List<ProjectTask> _lastTaskList = [];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public override Task SetUp()
    {
        _host = Context.GetResource<AlbaResource>().AlbaHost;
        return Task.CompletedTask;
    }

    [When("I dispatch a CreateTask command for project {string} title {string} assigned to {string}")]
    public async Task DispatchCreateTask(string projectName, string title, string assignedTo)
    {
        var session = await Context.InvokeMessageAndWaitAsync(
            new CreateTask(projectName, title, assignedTo));

        if (session.Status != Wolverine.Tracking.TrackingStatus.Completed)
            throw new Exception($"Message session did not complete: {session.Status}");
    }

    [Then("the task list contains {int} task")]
    public async Task TaskListContains(int expected)
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/tasks"));
        var json = await result.ReadAsTextAsync();
        _lastTaskList = JsonSerializer.Deserialize<List<ProjectTask>>(json, JsonOpts) ?? [];
        if (_lastTaskList.Count != expected)
            throw new Exception($"Expected {expected} tasks but got {_lastTaskList.Count}.");
    }

    [Then("the task title is {string}")]
    public void TaskTitleIs(string expected)
    {
        var task = _lastTaskList.FirstOrDefault();
        if (task?.Title != expected)
            throw new Exception($"Expected task title '{expected}' but got '{task?.Title}'.");
    }

    [Then("the task is assigned to {string}")]
    public void TaskAssignedTo(string expected)
    {
        var task = _lastTaskList.FirstOrDefault();
        if (task?.AssignedTo != expected)
            throw new Exception($"Expected assignee '{expected}' but got '{task?.AssignedTo}'.");
    }
}
