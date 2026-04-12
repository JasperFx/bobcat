using Alba;
using Bobcat;
using Bobcat.Runtime;
using Shouldly;

namespace CleanArchitectureTodos.Tests;

[FixtureTitle("Clean Architecture Todos")]
public class TodosFixture : Fixture
{
    private IAlbaHost _host = null!;

    private TodoList? _todoList;
    private TodoItem? _todoItem;
    private int _lastStatusCode;
    private TodosVm? _todosVm;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _todoList = null;
        _todoItem = null;
        _lastStatusCode = 0;
        _todosVm = null;
        return Task.CompletedTask;
    }

    // ── Given ────────────────────────────────────────────────────────────────

    // Each successive call overwrites _todoList — last one wins, which is correct for the
    // duplicate-title-update scenario where we want to update the second list.
    [Given("a todo list titled {string} exists")]
    public async Task CreateListGiven(string title)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateTodoListRequest(title, null)).ToUrl("/api/todolists");
            x.StatusCodeShouldBe(200);
        });
        _todoList = result.ReadAsJson<TodoList>()!;
    }

    [Given("a todo list titled {string} with colour {string} exists")]
    public async Task CreateListWithColourGiven(string title, string colour)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateTodoListRequest(title, colour)).ToUrl("/api/todolists");
            x.StatusCodeShouldBe(200);
        });
        _todoList = result.ReadAsJson<TodoList>()!;
    }

    [Given("a todo item titled {string} exists in the list")]
    public async Task CreateItemGiven(string title)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateTodoItemRequest(_todoList!.Id, title)).ToUrl("/api/todoitems");
            x.StatusCodeShouldBe(200);
        });
        _todoItem = result.ReadAsJson<TodoItem>()!;
    }

    // ── When ─────────────────────────────────────────────────────────────────

    [When("I create a todo list titled {string} with colour {string}")]
    public async Task CreateListWithColour(string title, string colour)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateTodoListRequest(title, colour)).ToUrl("/api/todolists");
            x.StatusCodeShouldBe(200);
        });
        _todoList = result.ReadAsJson<TodoList>()!;
        _lastStatusCode = 200;
    }

    [When("I create a todo list titled {string} with no colour")]
    public async Task CreateListNoColour(string title)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateTodoListRequest(title, null)).ToUrl("/api/todolists");
            x.StatusCodeShouldBe(200);
        });
        _todoList = result.ReadAsJson<TodoList>()!;
        _lastStatusCode = 200;
    }

    [When("I try to create a todo list titled {string} with no colour")]
    public async Task TryCreateListDuplicate(string title)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateTodoListRequest(title, null)).ToUrl("/api/todolists");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I update the todo list title to {string} and colour to {string}")]
    public async Task UpdateList(string title, string colour)
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateTodoListRequest(title, colour)).ToUrl($"/api/todolists/{_todoList!.Id}");
            x.StatusCodeShouldBe(200);
        });
        _todoList = result.ReadAsJson<TodoList>()!;
        _lastStatusCode = 200;
    }

    // _todoList is the most recently created list (the "second" one after two Given steps)
    [When("I try to update the second todo list title to {string}")]
    public async Task TryUpdateListDuplicate(string title)
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateTodoListRequest(title, null)).ToUrl($"/api/todolists/{_todoList!.Id}");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I delete the todo list")]
    public async Task DeleteList()
    {
        await _host.Scenario(x =>
        {
            x.Delete.Url($"/api/todolists/{_todoList!.Id}");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;
    }

    [When("I get all todo lists")]
    public async Task GetAllLists()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/api/todolists");
            x.StatusCodeShouldBe(200);
        });
        _todosVm = result.ReadAsJson<TodosVm>()!;
    }

    [When("I create a todo item titled {string} in the list")]
    public async Task CreateItem(string title)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateTodoItemRequest(_todoList!.Id, title)).ToUrl("/api/todoitems");
            x.StatusCodeShouldBe(200);
        });
        _todoItem = result.ReadAsJson<TodoItem>()!;
        _lastStatusCode = 200;
    }

    [When("I update the todo item title to {string} and mark it done")]
    public async Task UpdateItem(string title)
    {
        await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateTodoItemRequest(title, true)).ToUrl($"/api/todoitems/{_todoItem!.Id}");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;
    }

    [When("I update the todo item detail with priority {string} and note {string}")]
    public async Task UpdateItemDetail(string priority, string note)
    {
        var level = Enum.Parse<PriorityLevel>(priority);
        await _host.Scenario(x =>
        {
            x.Patch.Json(new UpdateTodoItemDetailRequest(_todoList!.Id, level, note))
                .ToUrl($"/api/todoitems/detail/{_todoItem!.Id}");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;
    }

    [When("I delete the todo item")]
    public async Task DeleteItem()
    {
        await _host.Scenario(x =>
        {
            x.Delete.Url($"/api/todoitems/{_todoItem!.Id}");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;
    }

    // ── Then / Check ─────────────────────────────────────────────────────────

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected) => _lastStatusCode.ShouldBe(expected);

    [Then("the todo list title should be {string}")]
    public void ListTitleShouldBe(string expected) => _todoList!.Title.ShouldBe(expected);

    [Then("the todo list colour should be {string}")]
    public void ListColourShouldBe(string expected) => _todoList!.Colour.ShouldBe(expected);

    [Then("there should be at least {int} todo lists")]
    public void ListCountAtLeast(int min) => (_todosVm!.Lists.Count >= min).ShouldBeTrue();

    [Check("the result should contain priority levels")]
    public bool HasPriorityLevels() => _todosVm?.PriorityLevels?.Any() == true;

    [Check("the result should contain colours")]
    public bool HasColours() => _todosVm?.Colours?.Any() == true;

    [Then("the todo item title should be {string}")]
    public void ItemTitleShouldBe(string expected) => _todoItem!.Title.ShouldBe(expected);

    [Check("the todo item should not be done")]
    public bool ItemNotDone() => _todoItem?.Done == false;
}
