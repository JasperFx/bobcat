using Bobcat;
using Bobcat.Alba;

namespace CleanArchitectureTodos.Tests;

/// <summary>
/// Specs for the Clean Architecture Todos sample. Reuses the host's own request, document and
/// view-model types via the project reference rather than re-declaring them locally — the file
/// this replaced declared a <c>CreateTodoListRequest(string Title)</c> against a host record that
/// also carries <c>Colour</c>, an <c>UpdateTodoListRequest(int Id, string Title)</c> POSTed to
/// what is a <c>[WolverinePut]</c>, a <c>CreateTodoItemResponse(int Id)</c> for an item whose id
/// is a <c>Guid</c>, read a bare list from a GET that returns a <c>TodosVm</c>, and asserted a
/// default colour nothing ever assigned. Nothing had ever compiled it, so nothing reported any
/// of that.
/// </summary>
[FixtureTitle("Clean Architecture Todos")]
public class CleanArchitectureTodosFixture : Fixture
{
    // The first list a scenario created — the one the unqualified steps ("the stored list",
    // "I delete the todo list") refer to. Lists created later in the same scenario are reached
    // by title through _listIdsByTitle.
    private int _listId;
    private readonly Dictionary<string, int> _listIdsByTitle = new();
    private Guid _itemId;
    private int _lastStatusCode;
    private IReadOnlyList<TodoListDto> _lists = [];

    public Task BeforeEach()
    {
        _listId = 0;
        _listIdsByTitle.Clear();
        _itemId = Guid.Empty;
        _lastStatusCode = 0;
        _lists = [];
        return Task.CompletedTask;
    }

    // ---- lists ----------------------------------------------------------------------------

    [Given("I create a todo list with title {string}")]
    public Task GivenCreateTodoList(string title) => createTodoListCore(title);

    [When("I create a todo list with title {string}")]
    public Task WhenCreateTodoList(string title) => createTodoListCore(title);

    private async Task createTodoListCore(string title)
    {
        // Colour is null here so the scenario about the default colour is actually exercising
        // the host's default, not one the fixture supplied.
        var result = await Context!.PostJsonAsync<CreateTodoListRequest, TodoList>(
            "/api/todolists", new CreateTodoListRequest(title, Colour: null));

        _lastStatusCode = result.StatusCode;

        // Only take the id from a success. A 400 ProblemDetails body deserializes into a
        // TodoList just fine (unknown properties are ignored), so "Body is not null" is not
        // evidence that anything was created. See docs/sample-wiring.md footgun 10.
        if (result.StatusCode is >= 200 and < 300 && result.Body is not null)
        {
            if (_listId == 0) _listId = result.Body.Id;
            _listIdsByTitle[title] = result.Body.Id;
        }
    }

    [When("I update the list title to {string}")]
    public Task UpdateListTitle(string newTitle) => updateListTitleCore(_listId, newTitle);

    [When("I update the list {string} title to {string}")]
    public Task UpdateNamedListTitle(string title, string newTitle)
        => updateListTitleCore(_listIdsByTitle[title], newTitle);

    private async Task updateListTitleCore(int listId, string newTitle)
    {
        // PUT, not POST — the endpoint is a [WolverinePut], and the request carries a Colour
        // as well as the title. Null leaves the stored colour alone.
        var result = await Context!.PutJsonAsync<UpdateTodoListRequest, TodoList>(
            $"/api/todolists/{listId}", new UpdateTodoListRequest(newTitle, Colour: null));

        _lastStatusCode = result.StatusCode;
    }

    [When("I delete the todo list")]
    public async Task DeleteTodoList()
    {
        var result = await Context!.DeleteAsync($"/api/todolists/{_listId}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I get all todo lists")]
    public async Task GetAllLists()
    {
        // The GET returns a view model wrapping the lists alongside the priority and colour
        // lookups, not a bare array of lists.
        var result = await Context!.GetJsonAsync<TodosVm>("/api/todolists");
        _lastStatusCode = result.StatusCode;
        _lists = result.Body?.Lists ?? [];
    }

    // ---- items ----------------------------------------------------------------------------

    [Given("I create a todo item with title {string}")]
    public Task GivenCreateTodoItem(string title) => createTodoItemCore(title);

    [When("I create a todo item with title {string}")]
    public Task WhenCreateTodoItem(string title) => createTodoItemCore(title);

    private async Task createTodoItemCore(string title)
    {
        var result = await Context!.PostJsonAsync<CreateTodoItemRequest, TodoItem>(
            "/api/todoitems", new CreateTodoItemRequest(_listId, title));

        _lastStatusCode = result.StatusCode;

        // Same guard as the list: TodoItem.Id is initialised to Guid.NewGuid(), so a
        // ProblemDetails body read as a TodoItem carries a perfectly plausible id for an item
        // that was never created.
        if (result.StatusCode is >= 200 and < 300 && result.Body is not null)
            _itemId = result.Body.Id;
    }

    [When("I update the todo item title to {string}")]
    public async Task UpdateTodoItem(string newTitle)
    {
        // PUT, not POST, and the request is (Title, Done) rather than (Id, Title) — the item id
        // travels in the route.
        var result = await Context!.PutJsonAsync<UpdateTodoItemRequest, object>(
            $"/api/todoitems/{_itemId}", new UpdateTodoItemRequest(newTitle, Done: false));

        _lastStatusCode = result.StatusCode;
    }

    [When("I delete the todo item")]
    public async Task DeleteTodoItem()
    {
        var result = await Context!.DeleteAsync($"/api/todoitems/{_itemId}");
        _lastStatusCode = result.StatusCode;
    }

    // ---- assertions -------------------------------------------------------------------------

    [Check("the response status is {int}")]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    /// <summary>
    /// Reads the list back over HTTP rather than trusting what the write echoed. Every "stored
    /// list" assertion goes through here, so a write that answered the right status but never
    /// reached Marten would still fail the scenario.
    /// </summary>
    private Task<HttpResult<TodoList>> loadList(int listId)
        => Context!.GetJsonAsync<TodoList>($"/api/todolists/{listId}");

    [Check("the stored list is titled {string}")]
    public async Task<bool> StoredListIsTitled(string title)
    {
        var result = await loadList(_listId);
        return result.Body is not null && result.Body.Title == title;
    }

    [Check("the stored list {string} is still titled {string}")]
    public async Task<bool> NamedStoredListIsTitled(string createdAs, string title)
    {
        var result = await loadList(_listIdsByTitle[createdAs]);
        return result.Body is not null && result.Body.Title == title;
    }

    [Check("the stored list has the colour {string}")]
    public async Task<bool> StoredListHasColour(string colour)
    {
        var result = await loadList(_listId);
        return result.Body is not null && result.Body.Colour == colour;
    }

    [Check("the list no longer exists")]
    public async Task<bool> ListNoLongerExists()
    {
        var result = await loadList(_listId);
        return result.StatusCode == 404;
    }

    [Check("{int} lists are returned")]
    public bool ListCount(int expected) => _lists.Count == expected;

    [Check("the stored list has an item titled {string}")]
    public async Task<bool> StoredListHasItemTitled(string title)
    {
        var result = await loadList(_listId);
        return result.Body is not null && result.Body.Items.Any(i => i.Title == title);
    }

    [Check("the stored list has {int} items")]
    public async Task<bool> StoredListHasItemCount(int expected)
    {
        var result = await loadList(_listId);
        return result.Body is not null && result.Body.Items.Count == expected;
    }
}
