using Administration;
using Alba;
using Bobcat;
using Bobcat.Runtime;
using Meetings;
using Payments;
using Registrations;
using Shouldly;
using UserAccess;

namespace MeetingGroupMonolith.Tests;

[FixtureTitle("Meeting Group Monolith")]
public class MeetingGroupFixture : Fixture
{
    private IAlbaHost _host = null!;

    private User? _user;
    private MeetingGroupProposal? _proposal;
    private MeetingGroup? _meetingGroup;
    private Meeting? _meeting;
    private List<MeetingGroup>? _meetingGroups;
    private List<Meeting>? _meetings;
    private Guid _subscriptionId;
    private int _lastStatusCode;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _user = null;
        _proposal = null;
        _meetingGroup = null;
        _meeting = null;
        _meetingGroups = null;
        _meetings = null;
        _subscriptionId = Guid.Empty;
        _lastStatusCode = 0;
        return Task.CompletedTask;
    }

    private async Task<MeetingGroupProposal> CreateAndAcceptProposal(string groupName, string city)
    {
        var proposal = (await _host.Scenario(x =>
        {
            x.Post.Json(new ProposeNewMeetingGroup(groupName, $"{groupName} group", city, "US", Guid.NewGuid()))
                .ToUrl("/api/administration/proposals");
            x.StatusCodeShouldBe(200);
        })).ReadAsJson<MeetingGroupProposal>()!;

        await _host.Scenario(x =>
        {
            x.Post.Json(new { }).ToUrl($"/api/administration/proposals/{proposal.Id}/accept");
            x.StatusCodeShouldBe(200);
        });

        // Wait for cascading handler to create the MeetingGroup
        await Task.Delay(1000);
        return proposal;
    }

    // ── Given ────────────────────────────────────────────────────────────────

    [Given("a meeting group proposal named {string} in {string} exists")]
    public async Task CreateProposalGiven(string name, string city)
    {
        _proposal = (await _host.Scenario(x =>
        {
            x.Post.Json(new ProposeNewMeetingGroup(name, $"{name} group", city, "US", Guid.NewGuid()))
                .ToUrl("/api/administration/proposals");
            x.StatusCodeShouldBe(200);
        })).ReadAsJson<MeetingGroupProposal>()!;
    }

    [Given("an accepted meeting group named {string} in {string} exists")]
    public async Task CreateAcceptedGroupGiven(string name, string city)
    {
        await CreateAndAcceptProposal(name, city);

        var groups = (await _host.Scenario(x =>
        {
            x.Get.Url("/api/meeting-groups");
            x.StatusCodeShouldBe(200);
        })).ReadAsJson<List<MeetingGroup>>()!;

        _meetingGroup = groups.First(g => g.Name == name);
    }

    [Given("a meeting titled {string} exists in the group")]
    public async Task CreateMeetingGiven(string title)
    {
        _meeting = (await _host.Scenario(x =>
        {
            x.Post.Json(new CreateMeeting(
                _meetingGroup!.Id, title, $"{title} description",
                DateTime.UtcNow.AddDays(3),
                DateTime.UtcNow.AddDays(3).AddHours(1),
                "123 Test Ave", 10, 5m
            )).ToUrl("/api/meetings");
            x.StatusCodeShouldBe(200);
        })).ReadAsJson<Meeting>()!;
    }

    // ── When ─────────────────────────────────────────────────────────────────

    [When("I register a user with login {string} email {string} and password {string}")]
    public async Task RegisterUser(string login, string email, string password)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterUser(login, email, "John", "Doe", password))
                .ToUrl("/api/registrations");
            x.StatusCodeShouldBe(200);
        });
        _user = result.ReadAsJson<User>()!;
        _lastStatusCode = 200;
    }

    [When("I try to register a user with invalid input")]
    public async Task TryRegisterInvalid()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterUser("", "", "", "", "short")).ToUrl("/api/registrations");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I propose a meeting group named {string} in {string}")]
    public async Task ProposeGroup(string name, string city)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new ProposeNewMeetingGroup(name, $"{name} group", city, "US", Guid.NewGuid()))
                .ToUrl("/api/administration/proposals");
            x.StatusCodeShouldBe(200);
        });
        _proposal = result.ReadAsJson<MeetingGroupProposal>()!;
        _lastStatusCode = 200;
    }

    [When("I accept the proposal")]
    public async Task AcceptProposal()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new { }).ToUrl($"/api/administration/proposals/{_proposal!.Id}/accept");
            x.StatusCodeShouldBe(200);
        });
        _proposal = result.ReadAsJson<MeetingGroupProposal>()!;
        _lastStatusCode = 200;
    }

    [When("I create a meeting titled {string} in the group")]
    public async Task CreateMeeting(string title)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateMeeting(
                _meetingGroup!.Id, title, $"{title} description",
                DateTime.UtcNow.AddDays(7),
                DateTime.UtcNow.AddDays(7).AddHours(2),
                "123 Conference Rd", 20, 0m
            )).ToUrl("/api/meetings");
            x.StatusCodeShouldBe(200);
        });
        _meeting = result.ReadAsJson<Meeting>()!;
        _lastStatusCode = 200;
    }

    [When("I add an attendee to the meeting")]
    public async Task AddAttendee()
    {
        var memberId = Guid.NewGuid();
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new AddAttendee(_meeting!.Id, memberId))
                .ToUrl($"/api/meetings/{_meeting.Id}/attendees");
            x.StatusCodeShouldBe(200);
        });
        _meeting = result.ReadAsJson<Meeting>()!;
        _lastStatusCode = 200;
    }

    [When("I get all meeting groups")]
    public async Task GetMeetingGroups()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/api/meeting-groups");
            x.StatusCodeShouldBe(200);
        });
        _meetingGroups = result.ReadAsJson<List<MeetingGroup>>()!;
    }

    [When("I get all meetings")]
    public async Task GetMeetings()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/api/meetings");
            x.StatusCodeShouldBe(200);
        });
        _meetings = result.ReadAsJson<List<Meeting>>()!;
    }

    [When("I create a subscription with period {string}")]
    public async Task CreateSubscription(string period)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateSubscription(Guid.NewGuid(), period))
                .ToUrl("/api/payments/subscriptions");
            x.StatusCodeShouldBe(200);
        });
        _subscriptionId = result.ReadAsJson<Guid>();
        _lastStatusCode = 200;
    }

    [When("I try to create a subscription with period {string}")]
    public async Task TryCreateSubscription(string period)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateSubscription(Guid.NewGuid(), period))
                .ToUrl("/api/payments/subscriptions");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    // ── Then / Check ─────────────────────────────────────────────────────────

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected) => _lastStatusCode.ShouldBe(expected);

    [Check("the user id should be valid")]
    public bool UserIdIsValid() => _user?.Id != Guid.Empty;

    [Then("the user login should be {string}")]
    public void UserLoginShouldBe(string expected) => _user!.Login.ShouldBe(expected);

    [Then("the user email should be {string}")]
    public void UserEmailShouldBe(string expected) => _user!.Email.ShouldBe(expected);

    [Then("the proposal name should be {string}")]
    public void ProposalNameShouldBe(string expected) => _proposal!.Name.ShouldBe(expected);

    [Then("the proposal status should be {string}")]
    public void ProposalStatusShouldBe(string expected) =>
        _proposal!.Status.ToString().ShouldBe(expected);

    [Check("the proposal decision date should be set")]
    public bool ProposalDecisionDateIsSet() => _proposal?.DecisionDate != null;

    [Then("the meeting title should be {string}")]
    public void MeetingTitleShouldBe(string expected) => _meeting!.Title.ShouldBe(expected);

    [Then("the meeting should have {int} attendee")]
    public void MeetingShouldHaveAttendees(int count) => _meeting!.Attendees.Count.ShouldBe(count);

    [Then("the meeting groups list should be returned")]
    public void MeetingGroupsListReturned() => _meetingGroups.ShouldNotBeNull();

    [Then("the meetings list should be returned")]
    public void MeetingsListReturned() => _meetings.ShouldNotBeNull();

    [Check("the subscription id should be valid")]
    public bool SubscriptionIdIsValid() => _subscriptionId != Guid.Empty;
}
