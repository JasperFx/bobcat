using System.Text.Json;
using Alba;
using Bobcat;
using Bobcat.Runtime;

namespace MoreSpeakers;

[FixtureTitle("Speaker Management")]
public class SpeakerFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;
    private Speaker? _lastSpeaker;
    private int _currentSpeakerId;
    private List<Speaker> _lastSpeakerList = [];
    private Session? _lastSession;
    private List<Session> _lastSessionList = [];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public override Task SetUp()
    {
        _host = Context.GetResource<AlbaResource>().AlbaHost;
        return Task.CompletedTask;
    }

    [When("I request the speaker list")]
    public async Task RequestSpeakerList()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/speakers"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastSpeakerList = JsonSerializer.Deserialize<List<Speaker>>(json, JsonOpts) ?? [];
    }

    [When("I add a speaker with name {string} bio {string} and topic {string}")]
    public async Task AddSpeaker(string name, string bio, string topic)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, bio, topic }).ToUrl("/api/speakers");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastSpeaker = JsonSerializer.Deserialize<Speaker>(json, JsonOpts);
        if (_lastSpeaker is not null) _currentSpeakerId = _lastSpeaker.Id;
    }

    [Given("a speaker exists with name {string} bio {string} and topic {string}")]
    public async Task SpeakerExists(string name, string bio, string topic)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, bio, topic }).ToUrl("/api/speakers");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        var speaker = JsonSerializer.Deserialize<Speaker>(json, JsonOpts)!;
        _currentSpeakerId = speaker.Id;
        _lastSpeaker = speaker;
    }

    [When("I get the speaker by id")]
    public async Task GetSpeakerById()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/speakers/{_currentSpeakerId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastSpeaker = JsonSerializer.Deserialize<Speaker>(json, JsonOpts);
    }

    [When("I update the speaker bio to {string}")]
    public async Task UpdateSpeakerBio(string bio)
    {
        var speaker = Store.GetSpeakerById(_currentSpeakerId)!;
        var result = await _host.Scenario(s =>
        {
            s.Put.Json(new { speaker.Name, bio, speaker.Topic })
                .ToUrl($"/api/speakers/{_currentSpeakerId}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastSpeaker = JsonSerializer.Deserialize<Speaker>(json, JsonOpts);
    }

    [When("I update the speaker topic to {string}")]
    public async Task UpdateSpeakerTopic(string topic)
    {
        var speaker = Store.GetSpeakerById(_currentSpeakerId)!;
        var result = await _host.Scenario(s =>
        {
            s.Put.Json(new { speaker.Name, speaker.Bio, topic })
                .ToUrl($"/api/speakers/{_currentSpeakerId}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastSpeaker = JsonSerializer.Deserialize<Speaker>(json, JsonOpts);
    }

    [When("I delete the speaker")]
    public async Task DeleteSpeaker()
    {
        var result = await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/speakers/{_currentSpeakerId}");
            s.StatusCodeShouldBe(204);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [Given("a session exists with title {string} and duration {int} minutes")]
    public async Task SessionExists(string title, int durationMinutes)
        => await AddSessionCore(title, durationMinutes);

    [When("I add a session with title {string} and duration {int} minutes")]
    public async Task AddSession(string title, int durationMinutes)
        => await AddSessionCore(title, durationMinutes);

    private async Task AddSessionCore(string title, int durationMinutes)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { title, durationMinutes }).ToUrl($"/api/speakers/{_currentSpeakerId}/sessions");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastSession = JsonSerializer.Deserialize<Session>(json, JsonOpts);
    }

    [When("I get the sessions for the speaker")]
    public async Task GetSessionsForSpeaker()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/speakers/{_currentSpeakerId}/sessions"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastSessionList = JsonSerializer.Deserialize<List<Session>>(json, JsonOpts) ?? [];
    }

    [When("I get sessions for a non-existent speaker with id {int}")]
    public async Task GetSessionsForNonExistentSpeaker(int id)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/speakers/{id}/sessions");
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

    [Then("the speaker list is empty")]
    public void SpeakerListIsEmpty()
    {
        if (_lastSpeakerList.Count != 0)
            throw new Exception($"Expected empty speaker list but got {_lastSpeakerList.Count} speakers.");
    }

    [Then("the speaker name is {string}")]
    public void SpeakerNameIs(string expected)
    {
        if (_lastSpeaker?.Name != expected)
            throw new Exception($"Expected speaker name '{expected}' but got '{_lastSpeaker?.Name}'.");
    }

    [Then("the speaker bio is {string}")]
    public void SpeakerBioIs(string expected)
    {
        if (_lastSpeaker?.Bio != expected)
            throw new Exception($"Expected speaker bio '{expected}' but got '{_lastSpeaker?.Bio}'.");
    }

    [Then("the speaker topic is {string}")]
    public void SpeakerTopicIs(string expected)
    {
        if (_lastSpeaker?.Topic != expected)
            throw new Exception($"Expected speaker topic '{expected}' but got '{_lastSpeaker?.Topic}'.");
    }

    [Then("the session title is {string}")]
    public void SessionTitleIs(string expected)
    {
        if (_lastSession?.Title != expected)
            throw new Exception($"Expected session title '{expected}' but got '{_lastSession?.Title}'.");
    }

    [Then("the session duration is {int} minutes")]
    public void SessionDurationIs(int expected)
    {
        if (_lastSession?.DurationMinutes != expected)
            throw new Exception($"Expected session duration '{expected}' but got '{_lastSession?.DurationMinutes}'.");
    }

    [Then("the session list has {int} items")]
    public void SessionListHasCount(int expected)
    {
        if (_lastSessionList.Count != expected)
            throw new Exception($"Expected {expected} sessions but got {_lastSessionList.Count}.");
    }

    private void AssertStatus(int expected)
    {
        if (_lastStatusCode != expected)
            throw new Exception($"Expected HTTP {expected} but got {_lastStatusCode}.");
    }
}
