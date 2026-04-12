using Bobcat;
using Bobcat.Alba;

namespace OutboxDemo.Tests;

[FixtureTitle("Outbox Demo")]
public class OutboxDemoFixture
{
    private int _lastStatusCode;
    private bool _eventPersisted;

    [When("I submit a member joined event for member {string} to group {string}")]
    [Given("I submit a member joined event for member {string} to group {string}")]
    public async Task SubmitMemberJoined(IStepContext context, string memberId, string groupId)
    {
        var result = await context.PostJsonAsync<MemberJoinedEvent, object>(
            "/api/meetings/member-joined",
            new MemberJoinedEvent(memberId, groupId));
        _lastStatusCode = result.StatusCode;
    }

    [When("I submit a member left event for member {string} to group {string}")]
    public async Task SubmitMemberLeft(IStepContext context, string memberId, string groupId)
    {
        var result = await context.PostJsonAsync<MemberLeftEvent, object>(
            "/api/meetings/member-left",
            new MemberLeftEvent(memberId, groupId));
        _lastStatusCode = result.StatusCode;
    }

    [Then("the event is stored in the outbox")]
    public async Task VerifyEventStoredInOutbox(IStepContext context)
    {
        // Query the outbox table or verify via Wolverine tracking
        var result = await context.GetJsonAsync<List<OutboxEventDto>>("/api/outbox/events");
        _eventPersisted = result.Body?.Count > 0;
    }

    [Then("the response status is {int}")]
    [Check]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Then("the event is persisted")]
    [Check]
    public bool EventIsStored() => _eventPersisted;
}

record MemberJoinedEvent(string MemberId, string GroupId);
record MemberLeftEvent(string MemberId, string GroupId);
record OutboxEventDto(Guid Id, string EventType, string Payload);
