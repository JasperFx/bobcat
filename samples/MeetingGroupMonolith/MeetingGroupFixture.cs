using System.Text.Json;
using Alba;
using Bobcat;
using Bobcat.Runtime;

namespace MeetingGroupMonolith;

[FixtureTitle("Meeting Groups")]
public class MeetingGroupFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;
    private Group? _lastGroup;
    private Event? _lastEvent;
    private int _currentGroupId;
    private int _currentEventId;
    private List<Group> _lastGroupList = [];
    private List<Member> _lastMemberList = [];
    private List<Event> _lastEventList = [];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public override Task SetUp()
    {
        _host = Context.GetResource<AlbaResource>().AlbaHost;
        return Task.CompletedTask;
    }

    // --- Group steps ---

    [When("I create a group named {string} in {string}")]
    public async Task CreateGroup(string name, string city)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, description = $"A group for {name}", city }).ToUrl("/api/groups");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastGroup = JsonSerializer.Deserialize<Group>(json, JsonOpts);
        if (_lastGroup is not null) _currentGroupId = _lastGroup.Id;
    }

    [Given("a group named {string} exists in {string}")]
    public async Task GroupExists(string name, string city)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, description = $"A group for {name}", city }).ToUrl("/api/groups");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        _lastGroup = JsonSerializer.Deserialize<Group>(json, JsonOpts)!;
        _currentGroupId = _lastGroup.Id;
    }

    [When("I get the group details")]
    public async Task GetGroupDetails()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/groups/{_currentGroupId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastGroup = JsonSerializer.Deserialize<Group>(json, JsonOpts);
    }

    [When("I list all groups")]
    public async Task ListAllGroups()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/groups"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastGroupList = JsonSerializer.Deserialize<List<Group>>(json, JsonOpts) ?? [];
    }

    // --- Member steps ---

    [When("I join the group as {string}")]
    public async Task JoinGroup(string memberName)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { memberName }).ToUrl($"/api/groups/{_currentGroupId}/members");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [Given("{string} joins the group")]
    public async Task MemberJoinsGroup(string memberName)
    {
        await _host.Scenario(s =>
        {
            s.Post.Json(new { memberName }).ToUrl($"/api/groups/{_currentGroupId}/members");
            s.StatusCodeShouldBe(201);
        });
    }

    [When("I list the group members")]
    public async Task ListGroupMembers()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/groups/{_currentGroupId}/members"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastMemberList = JsonSerializer.Deserialize<List<Member>>(json, JsonOpts) ?? [];
    }

    // --- Event steps ---

    [When("I schedule an event {string} on {string} with max {int} attendees")]
    public async Task ScheduleEvent(string title, string date, int maxAttendees)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { title, date, location = "Main Hall", maxAttendees }).ToUrl($"/api/groups/{_currentGroupId}/events");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastEvent = JsonSerializer.Deserialize<Event>(json, JsonOpts);
        if (_lastEvent is not null) _currentEventId = _lastEvent.Id;
    }

    [Given("an event {string} exists on {string} with max {int} attendees")]
    public async Task EventExists(string title, string date, int maxAttendees)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { title, date, location = "Main Hall", maxAttendees }).ToUrl($"/api/groups/{_currentGroupId}/events");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        _lastEvent = JsonSerializer.Deserialize<Event>(json, JsonOpts)!;
        _currentEventId = _lastEvent.Id;
    }

    [When("I list group events")]
    public async Task ListGroupEvents()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/groups/{_currentGroupId}/events"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastEventList = JsonSerializer.Deserialize<List<Event>>(json, JsonOpts) ?? [];
    }

    [When("I attend the event")]
    public async Task AttendEvent()
    {
        var result = await _host.Scenario(s =>
            s.Post.Url($"/api/groups/{_currentGroupId}/events/{_currentEventId}/attend"));
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I try to attend the full event")]
    public async Task TryAttendFullEvent()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Url($"/api/groups/{_currentGroupId}/events/{_currentEventId}/attend");
            s.StatusCodeShouldBe(400);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    // --- Then steps ---

    [Then("the response is 200 OK")]
    public void ResponseIs200() => AssertStatus(200);

    [Then("the response is 201 Created")]
    public void ResponseIs201() => AssertStatus(201);

    [Then("the response is 400 Bad Request")]
    public void ResponseIs400() => AssertStatus(400);

    [Then("the group name is {string}")]
    public void GroupNameIs(string expected)
    {
        if (_lastGroup?.Name != expected)
            throw new Exception($"Expected group name '{expected}' but got '{_lastGroup?.Name}'.");
    }

    [Then("the group city is {string}")]
    public void GroupCityIs(string expected)
    {
        if (_lastGroup?.City != expected)
            throw new Exception($"Expected group city '{expected}' but got '{_lastGroup?.City}'.");
    }

    [Then("the group list has {int} group")]
    public void GroupListHasOne(int count) => GroupListHasCount(count);

    [Then("the group list has {int} groups")]
    public void GroupListHasCount(int count)
    {
        if (_lastGroupList.Count != count)
            throw new Exception($"Expected {count} group(s) but got {_lastGroupList.Count}.");
    }

    [Then("the member list has {int} member")]
    public void MemberListHasOne(int count) => MemberListHasCount(count);

    [Then("the member list has {int} members")]
    public void MemberListHasCount(int count)
    {
        if (_lastMemberList.Count != count)
            throw new Exception($"Expected {count} member(s) but got {_lastMemberList.Count}.");
    }

    [Then("the event list has {int} event")]
    public void EventListHasOne(int count) => EventListHasCount(count);

    [Then("the event list has {int} events")]
    public void EventListHasCount(int count)
    {
        if (_lastEventList.Count != count)
            throw new Exception($"Expected {count} event(s) but got {_lastEventList.Count}.");
    }

    [Then("the event title is {string}")]
    public void EventTitleIs(string expected)
    {
        if (_lastEvent?.Title != expected)
            throw new Exception($"Expected event title '{expected}' but got '{_lastEvent?.Title}'.");
    }

    private void AssertStatus(int expected)
    {
        if (_lastStatusCode != expected)
            throw new Exception($"Expected HTTP {expected} but got {_lastStatusCode}.");
    }
}
