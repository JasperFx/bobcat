using Bobcat;
using Bobcat.Alba;

namespace CleanArchitectureTodos.Tests;

[FixtureTitle("Clean Architecture Todos")]
public class CleanArchitectureTodosFixture
{
    private int _listId;
    private int _itemId;
    private int _secondListId;
    private int _lastStatusCode;
    private string _defaultColour = "";
    private List<TodoListDto> _allLists = [];

    [Given("I create a todo list with title {string}")]
    [When("I create a todo list with title {string}")]
    public async Task CreateTodoList(IStepContext context, string title)
    {
        var result = await context.PostJsonAsync<CreateTodoListRequest, CreateTodoListResponse>(
            "/api/TodoLists",
            new CreateTodoListRequest(title));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
        {
            if (_listId == 0)
                _listId = result.Body.Id;
            else
                _secondListId = result.Body.Id;
        }
    }

    [When("I update the list title to {string}")]
    public async Task UpdateListTitle(IStepContext context, string newTitle)
    {
        var result = await context.PostJsonAsync<UpdateTodoListRequest, object>(
            $"/api/TodoLists/{_listId}",
            new UpdateTodoListRequest(_listId, newTitle));
        _lastStatusCode = result.StatusCode;
    }

    [When("I update the list {string} title to {string}")]
    public async Task UpdateSecondListTitle(IStepContext context, string which, string newTitle)
    {
        var id = which == "Second" ? _secondListId : _listId;
        var result = await context.PostJsonAsync<UpdateTodoListRequest, object>(
            $"/api/TodoLists/{id}",
            new UpdateTodoListRequest(id, newTitle));
        _lastStatusCode = result.StatusCode;
    }

    [When("I delete the todo list")]
    public async Task DeleteTodoList(IStepContext context)
    {
        var result = await context.DeleteAsync($"/api/TodoLists/{_listId}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I get all todo lists")]
    public async Task GetAllLists(IStepContext context)
    {
        var result = await context.GetJsonAsync<List<TodoListDto>>("/api/TodoLists");
        _lastStatusCode = result.StatusCode;
        _allLists = result.Body ?? [];
    }

    [When("I create a todo item with title {string}")]
    [Given("I create a todo item with title {string}")]
    public async Task CreateTodoItem(IStepContext context, string title)
    {
        var result = await context.PostJsonAsync<CreateTodoItemRequest, CreateTodoItemResponse>(
            "/api/TodoItems",
            new CreateTodoItemRequest(_listId, title));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _itemId = result.Body.Id;
    }

    [When("I update the todo item title to {string}")]
    public async Task UpdateTodoItem(IStepContext context, string newTitle)
    {
        var result = await context.PostJsonAsync<UpdateTodoItemRequest, object>(
            $"/api/TodoItems/{_itemId}",
            new UpdateTodoItemRequest(_itemId, newTitle));
        _lastStatusCode = result.StatusCode;
    }

    [When("I delete the todo item")]
    public async Task DeleteTodoItem(IStepContext context)
    {
        var result = await context.DeleteAsync($"/api/TodoItems/{_itemId}");
        _lastStatusCode = result.StatusCode;
    }

    [Then("the response status is {int}")]
    [Check]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Then("the list id is returned")]
    [Check]
    public bool ListIdReturned() => _listId > 0;

    [Then("the list has the default colour")]
    [Check]
    public bool HasDefaultColour() => !string.IsNullOrEmpty(_defaultColour);

    [Then("at least {int} lists are returned")]
    [Check]
    public bool AtLeastNLists(int min) => _allLists.Count >= min;
}

record CreateTodoListRequest(string Title);
record CreateTodoListResponse(int Id);
record UpdateTodoListRequest(int Id, string Title);
record TodoListDto(int Id, string Title, string? Colour);
record CreateTodoItemRequest(int ListId, string Title);
record CreateTodoItemResponse(int Id);
record UpdateTodoItemRequest(int Id, string Title);
