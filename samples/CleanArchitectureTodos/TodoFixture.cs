using System.Text.Json;
using Alba;
using Bobcat;
using Bobcat.Runtime;

namespace CleanArchitectureTodos;

[FixtureTitle("Todo Management")]
public class TodoFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;
    private TodoItem? _lastTodo;
    private int _currentTodoId;
    private List<TodoItem> _lastTodoList = [];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public override Task SetUp()
    {
        _host = Context.GetResource<AlbaResource>().AlbaHost;
        return Task.CompletedTask;
    }

    [When("I create a todo with title {string}")]
    public async Task CreateTodo(string title)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { title, description = (string?)null }).ToUrl("/api/todos");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastTodo = JsonSerializer.Deserialize<TodoItem>(json, JsonOpts);
        if (_lastTodo is not null) _currentTodoId = _lastTodo.Id;
    }

    [When("I create a todo with title {string} and description {string}")]
    public async Task CreateTodoWithDescription(string title, string description)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { title, description }).ToUrl("/api/todos");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastTodo = JsonSerializer.Deserialize<TodoItem>(json, JsonOpts);
        if (_lastTodo is not null) _currentTodoId = _lastTodo.Id;
    }

    [Given("a todo exists with title {string}")]
    public async Task TodoExists(string title)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { title, description = (string?)null }).ToUrl("/api/todos");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        var todo = JsonSerializer.Deserialize<TodoItem>(json, JsonOpts)!;
        _currentTodoId = todo.Id;
    }

    [When("I get the todo by id")]
    public async Task GetTodoById()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/todos/{_currentTodoId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastTodo = JsonSerializer.Deserialize<TodoItem>(json, JsonOpts);
    }

    [When("I get a todo that does not exist")]
    public async Task GetNonExistentTodo()
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url("/api/todos/99999");
            s.StatusCodeShouldBe(404);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I list all todos")]
    public async Task ListAllTodos()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/todos"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastTodoList = JsonSerializer.Deserialize<List<TodoItem>>(json, JsonOpts) ?? [];
    }

    [When("I list only completed todos")]
    public async Task ListCompletedTodos()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/todos?completed=true"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastTodoList = JsonSerializer.Deserialize<List<TodoItem>>(json, JsonOpts) ?? [];
    }

    [When("I mark the todo as complete")]
    public async Task MarkTodoComplete()
    {
        var result = await _host.Scenario(s =>
        {
            s.Patch.Url($"/api/todos/{_currentTodoId}/complete");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastTodo = JsonSerializer.Deserialize<TodoItem>(json, JsonOpts);
    }

    [When("I update the todo title to {string}")]
    public async Task UpdateTodoTitle(string newTitle)
    {
        var result = await _host.Scenario(s =>
        {
            s.Put.Json(new { title = newTitle, description = (string?)null }).ToUrl($"/api/todos/{_currentTodoId}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastTodo = JsonSerializer.Deserialize<TodoItem>(json, JsonOpts);
    }

    [When("I delete the todo")]
    public async Task DeleteTodo()
    {
        var result = await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/todos/{_currentTodoId}");
            s.StatusCodeShouldBe(204);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I delete a todo that does not exist")]
    public async Task DeleteNonExistentTodo()
    {
        var result = await _host.Scenario(s =>
        {
            s.Delete.Url("/api/todos/99999");
            s.StatusCodeShouldBe(404);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [Then("the response is 200 OK")]
    public void ResponseIs200() => AssertStatus(200);

    [Then("the response is 201 Created")]
    public void ResponseIs201() => AssertStatus(201);

    [Then("the response is 204 No Content")]
    public void ResponseIs204() => AssertStatus(204);

    [Then("the response is 404 Not Found")]
    public void ResponseIs404() => AssertStatus(404);

    [Then("the todo title is {string}")]
    public void TodoTitleIs(string expected)
    {
        if (_lastTodo?.Title != expected)
            throw new Exception($"Expected todo title '{expected}' but got '{_lastTodo?.Title}'.");
    }

    [Then("the todo description is {string}")]
    public void TodoDescriptionIs(string expected)
    {
        if (_lastTodo?.Description != expected)
            throw new Exception($"Expected todo description '{expected}' but got '{_lastTodo?.Description}'.");
    }

    [Then("the todo is completed")]
    public void TodoIsCompleted()
    {
        if (_lastTodo?.Completed != true)
            throw new Exception("Expected todo to be completed but it was not.");
    }

    [Then("the todo list has {int} items")]
    public void TodoListHasItems(int count)
    {
        if (_lastTodoList.Count != count)
            throw new Exception($"Expected {count} todos but got {_lastTodoList.Count}.");
    }

    [Then("all listed todos are completed")]
    public void AllListedTodosAreCompleted()
    {
        var notCompleted = _lastTodoList.Where(t => !t.Completed).ToList();
        if (notCompleted.Any())
            throw new Exception($"Expected all todos to be completed but {notCompleted.Count} were not.");
    }

    private void AssertStatus(int expected)
    {
        if (_lastStatusCode != expected)
            throw new Exception($"Expected HTTP {expected} but got {_lastStatusCode}.");
    }
}
