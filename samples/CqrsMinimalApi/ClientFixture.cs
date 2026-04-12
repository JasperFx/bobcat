using System.Net;
using System.Text.Json;
using Alba;
using Bobcat;
using Bobcat.Runtime;

namespace CqrsMinimalApi;

[FixtureTitle("Client Management")]
public class ClientFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;
    private Client? _lastClient;
    private int _currentClientId;
    private List<Client> _lastClientList = [];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public override Task SetUp()
    {
        _host = Context.GetResource<AlbaResource>().AlbaHost;
        return Task.CompletedTask;
    }

    [When("I request the client list")]
    public async Task RequestClientList()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/clients"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastClientList = JsonSerializer.Deserialize<List<Client>>(json, JsonOpts) ?? [];
    }

    [When("I create a client with name {string} and email {string}")]
    public async Task CreateClient(string name, string email)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, email }).ToUrl("/api/clients");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastClient = JsonSerializer.Deserialize<Client>(json, JsonOpts);
        if (_lastClient is not null) _currentClientId = _lastClient.Id;
    }

    [Given("a client exists with name {string} and email {string}")]
    public async Task ClientExists(string name, string email)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, email }).ToUrl("/api/clients");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        var client = JsonSerializer.Deserialize<Client>(json, JsonOpts)!;
        _currentClientId = client.Id;
    }

    [When("I get the client by id")]
    public async Task GetClientById()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/clients/{_currentClientId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastClient = JsonSerializer.Deserialize<Client>(json, JsonOpts);
    }

    [When("I update the client name to {string} and email to {string}")]
    public async Task UpdateClient(string name, string email)
    {
        var result = await _host.Scenario(s =>
        {
            s.Put.Json(new { name, email }).ToUrl($"/api/clients/{_currentClientId}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastClient = JsonSerializer.Deserialize<Client>(json, JsonOpts);
    }

    [When("I delete the client")]
    public async Task DeleteClient()
    {
        var result = await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/clients/{_currentClientId}");
            s.StatusCodeShouldBe(204);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [Then("the response is 200 OK")]
    public void ResponseIs200() => AssertStatus(200);

    [Then("the response is 201 Created")]
    public void ResponseIs201() => AssertStatus(201);

    [Then("the response is 204 No Content")]
    public void ResponseIs204() => AssertStatus(204);

    [Then("the client list is empty")]
    public void ClientListIsEmpty()
    {
        if (_lastClientList.Count != 0)
            throw new Exception($"Expected empty client list but got {_lastClientList.Count} clients.");
    }

    [Then("the client name is {string}")]
    public void ClientNameIs(string expected)
    {
        if (_lastClient?.Name != expected)
            throw new Exception($"Expected client name '{expected}' but got '{_lastClient?.Name}'.");
    }

    [Then("the client email is {string}")]
    public void ClientEmailIs(string expected)
    {
        if (_lastClient?.Email != expected)
            throw new Exception($"Expected client email '{expected}' but got '{_lastClient?.Email}'.");
    }

    private void AssertStatus(int expected)
    {
        if (_lastStatusCode != expected)
            throw new Exception($"Expected HTTP {expected} but got {_lastStatusCode}.");
    }
}
