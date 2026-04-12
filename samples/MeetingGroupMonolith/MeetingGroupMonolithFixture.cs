using Bobcat;
using Bobcat.Alba;

namespace MeetingGroupMonolith.Tests;

[FixtureTitle("Meeting Group Monolith")]
public class MeetingGroupMonolithFixture
{
    private Guid _userId;
    private string _accessToken = "";
    private Guid _proposalId;
    private Guid _groupId;
    private Guid _meetingId;
    private int _lastStatusCode;
    private List<object> _meetingGroups = [];
    private List<object> _meetings = [];

    [When("I register a user with email {string} and password {string}")]
    [Given("I register a user with email {string} and password {string}")]
    public async Task RegisterUser(IStepContext context, string email, string password)
    {
        var result = await context.PostJsonAsync<RegisterUserRequest, RegisterUserResponse>(
            "/api/users/register",
            new RegisterUserRequest(email, password));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
        {
            _userId = result.Body.UserId;
            _accessToken = result.Body.AccessToken ?? "";
        }
    }

    [When("I register a user with empty email and password {string}")]
    public async Task RegisterUserInvalid(IStepContext context, string password)
    {
        var result = await context.PostJsonAsync<RegisterUserRequest, object>(
            "/api/users/register",
            new RegisterUserRequest("", password));
        _lastStatusCode = result.StatusCode;
    }

    [When("I propose a meeting group named {string} in location {string}")]
    [Given("I propose a meeting group named {string} in location {string}")]
    public async Task ProposeMeetingGroup(IStepContext context, string name, string location)
    {
        var result = await context.PostJsonAsync<ProposeMeetingGroupRequest, ProposeMeetingGroupResponse>(
            "/api/meeting-groups/proposals",
            new ProposeMeetingGroupRequest(name, location));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _proposalId = result.Body.ProposalId;
    }

    [When("I accept the meeting group proposal")]
    [Given("I accept the meeting group proposal")]
    public async Task AcceptProposal(IStepContext context)
    {
        var result = await context.PostJsonAsync<AcceptProposalRequest, AcceptProposalResponse>(
            $"/api/meeting-groups/proposals/{_proposalId}/accept",
            new AcceptProposalRequest(_proposalId));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _groupId = result.Body.GroupId;
    }

    [When("I create a meeting named {string} in the group")]
    [Given("I create a meeting named {string} in the group")]
    public async Task CreateMeeting(IStepContext context, string name)
    {
        var result = await context.PostJsonAsync<CreateMeetingRequest, CreateMeetingResponse>(
            $"/api/meeting-groups/{_groupId}/meetings",
            new CreateMeetingRequest(name, DateTime.UtcNow.AddDays(7)));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _meetingId = result.Body.MeetingId;
    }

    [When("I add myself as an attendee")]
    public async Task AddAttendee(IStepContext context)
    {
        var result = await context.PostJsonAsync<AddAttendeeRequest, object>(
            $"/api/meetings/{_meetingId}/attendees",
            new AddAttendeeRequest(_userId));
        _lastStatusCode = result.StatusCode;
    }

    [When("I get all meeting groups")]
    public async Task GetMeetingGroups(IStepContext context)
    {
        var result = await context.GetJsonAsync<List<object>>("/api/meeting-groups");
        _lastStatusCode = result.StatusCode;
        _meetingGroups = result.Body ?? [];
    }

    [When("I get all meetings for the group")]
    public async Task GetMeetings(IStepContext context)
    {
        var result = await context.GetJsonAsync<List<object>>($"/api/meeting-groups/{_groupId}/meetings");
        _lastStatusCode = result.StatusCode;
        _meetings = result.Body ?? [];
    }

    [When("I create a subscription for {int} month")]
    public async Task CreateSubscription(IStepContext context, int months)
    {
        var result = await context.PostJsonAsync<CreateSubscriptionRequest, object>(
            "/api/subscriptions",
            new CreateSubscriptionRequest(_userId, months));
        _lastStatusCode = result.StatusCode;
    }

    [When("I create a subscription with period of {int} months")]
    public async Task CreateSubscriptionInvalid(IStepContext context, int months)
    {
        var result = await context.PostJsonAsync<CreateSubscriptionRequest, object>(
            "/api/subscriptions",
            new CreateSubscriptionRequest(_userId, months));
        _lastStatusCode = result.StatusCode;
    }

    [Then("the response status is {int}")]
    [Check]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Then("the user id is returned")]
    [Check]
    public bool UserIdReturned() => _userId != Guid.Empty;

    [Then("at least {int} meeting group is returned")]
    [Check]
    public bool AtLeastNGroups(int min) => _meetingGroups.Count >= min;

    [Then("at least {int} meeting is returned")]
    [Check]
    public bool AtLeastNMeetings(int min) => _meetings.Count >= min;
}

record RegisterUserRequest(string Email, string Password);
record RegisterUserResponse(Guid UserId, string? AccessToken);
record ProposeMeetingGroupRequest(string Name, string Location);
record ProposeMeetingGroupResponse(Guid ProposalId);
record AcceptProposalRequest(Guid ProposalId);
record AcceptProposalResponse(Guid GroupId);
record CreateMeetingRequest(string Name, DateTime StartDate);
record CreateMeetingResponse(Guid MeetingId);
record AddAttendeeRequest(Guid UserId);
record CreateSubscriptionRequest(Guid UserId, int MonthsCount);
