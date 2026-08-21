using Administration;
using Bobcat;
using Bobcat.Alba;
using Meetings;
using Payments;
using Registrations;
using UserAccess;
using Wolverine;
using Wolverine.Tracking;

namespace MeetingGroupMonolith.Tests;

/// <summary>
/// Specs for the modular-monolith-with-ddd conversion. Reuses the host's own command and document
/// types via the project reference rather than re-declaring request records locally — the file
/// this replaced posted a <c>RegisterUserRequest(Email, Password)</c> to <c>/api/users/register</c>
/// against a host whose only registration endpoint is <c>POST /api/registrations</c> taking
/// <c>RegisterUser(Login, Email, FirstName, LastName, Password)</c>, and so on for every other
/// step. Nothing had ever compiled it, so nothing reported the drift.
/// </summary>
[FixtureTitle("Meeting Group Monolith")]
public class MeetingGroupMonolithFixture : Fixture
{
    private Guid _userId;
    private Guid _proposalId;
    private Guid _meetingId;
    private Guid _subscriptionId;
    private int _lastStatusCode;
    private List<MeetingGroup> _meetingGroups = [];
    private List<Meeting> _meetings = [];

    public Task BeforeEach()
    {
        _userId = Guid.Empty;
        _proposalId = Guid.Empty;
        _meetingId = Guid.Empty;
        _subscriptionId = Guid.Empty;
        _lastStatusCode = 0;
        _meetingGroups = [];
        _meetings = [];
        return Task.CompletedTask;
    }

    // ---- registration -----------------------------------------------------------------------

    [Given("I register a user with email {string} and password {string}")]
    public Task GivenRegister(string email, string password) => registerCore(email, password);

    [When("I register a user with email {string} and password {string}")]
    public Task WhenRegister(string email, string password) => registerCore(email, password);

    [When("I register a user with empty email and password {string}")]
    public Task RegisterWithEmptyEmail(string password) => registerCore(string.Empty, password);

    private async Task registerCore(string email, string password)
    {
        // The spec names the email; the login is the part before the @, and the names are
        // filler the validator insists on but no scenario cares about.
        var login = email.Split('@')[0];

        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<RegisterUser, User>(
                "/api/registrations",
                new RegisterUser(login, email, "Test", "User", password)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _userId = result.Body.Id;
    }

    // ---- proposals --------------------------------------------------------------------------

    [Given("I propose a meeting group named {string} in {string}, {string}")]
    public Task GivenPropose(string name, string city, string countryCode)
        => proposeCore(name, city, countryCode);

    [When("I propose a meeting group named {string} in {string}, {string}")]
    public Task WhenPropose(string name, string city, string countryCode)
        => proposeCore(name, city, countryCode);

    private async Task proposeCore(string name, string city, string countryCode)
    {
        var result = await Context!.PostJsonAsync<ProposeNewMeetingGroup, ProposalCreation>(
            "/api/administration/proposals",
            new ProposeNewMeetingGroup(name, $"A group about {name}", city, countryCode, _userId));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _proposalId = result.Body.Id;
    }

    [Given("I accept the meeting group proposal")]
    public Task GivenAccept() => acceptCore();

    [When("I accept the meeting group proposal")]
    public Task WhenAccept() => acceptCore();

    private async Task acceptCore()
    {
        // Accepting cascades MeetingGroupProposalAcceptedEvent to the Meetings module, which
        // creates the group under the proposal's id.
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<object, MeetingGroupProposal>(
                $"/api/administration/proposals/{_proposalId}/accept",
                new { }));

        _lastStatusCode = result.StatusCode;
    }

    // ---- meetings ---------------------------------------------------------------------------

    [Given("I create a meeting named {string} in the group")]
    public Task GivenCreateMeeting(string title) => createMeetingCore(title);

    [When("I create a meeting named {string} in the group")]
    public Task WhenCreateMeeting(string title) => createMeetingCore(title);

    private async Task createMeetingCore(string title)
    {
        var start = DateTime.UtcNow.AddDays(7);
        var result = await Context!.PostJsonAsync<CreateMeeting, MeetingCreation>(
            "/api/meetings",
            new CreateMeeting(_proposalId, title, $"About {title}", start, start.AddHours(2),
                "Main Street 1", AttendeesLimit: null, Fee: 0m));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _meetingId = result.Body.Id;
    }

    [Given("I add myself as an attendee")]
    public Task GivenAddAttendee() => addAttendeeCore();

    [When("I add myself as an attendee")]
    public Task WhenAddAttendee() => addAttendeeCore();

    private async Task addAttendeeCore()
    {
        // Cascades MeetingAttendeeAddedEvent to the Payments module, which starts a fee stream.
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<AddAttendee, Meeting>(
                $"/api/meetings/{_meetingId}/attendees",
                new AddAttendee(_meetingId, _userId)));

        _lastStatusCode = result.StatusCode;
    }

    // ---- queries ----------------------------------------------------------------------------

    [When("I get all meeting groups")]
    public async Task GetMeetingGroups()
    {
        var result = await Context!.GetJsonAsync<List<MeetingGroup>>("/api/meeting-groups");
        _lastStatusCode = result.StatusCode;
        _meetingGroups = result.Body ?? [];
    }

    [When("I get all meetings for the group")]
    public async Task GetMeetings()
    {
        var result = await Context!.GetJsonAsync<List<Meeting>>($"/api/meeting-groups/{_proposalId}/meetings");
        _lastStatusCode = result.StatusCode;
        _meetings = result.Body ?? [];
    }

    // ---- subscriptions ----------------------------------------------------------------------

    [When("I create a {word} subscription")]
    public async Task CreateSubscription(string period)
    {
        // Cascades SubscriptionExpirationChangedEvent back to the Meetings module, which stamps
        // the new expiration date on the Member.
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<CreateSubscription, SubscriptionCreation>(
                "/api/payments/subscriptions",
                new CreateSubscription(_userId, period)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _subscriptionId = result.Body.Id;
    }

    // ---- assertions -------------------------------------------------------------------------

    [Check("the response status is {int}")]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Check("a member exists in the Meetings module for that user")]
    public async Task<bool> MemberExists()
    {
        var result = await Context!.GetJsonAsync<Member>($"/api/members/{_userId}");
        return result.StatusCode == 200 && result.Body?.Id == _userId;
    }

    [Check("the proposal is in verification")]
    public Task<bool> ProposalInVerification() => proposalStatusIs(ProposalStatus.InVerification);

    [Check("the proposal is accepted")]
    public Task<bool> ProposalAccepted() => proposalStatusIs(ProposalStatus.Accepted);

    private async Task<bool> proposalStatusIs(ProposalStatus expected)
    {
        var result = await Context!.GetJsonAsync<MeetingGroupProposal>($"/api/administration/proposals/{_proposalId}");
        return result.StatusCode == 200 && result.Body?.Status == expected;
    }

    [Check("a meeting group named {string} exists for the proposal")]
    public async Task<bool> GroupExistsForProposal(string name)
    {
        var result = await Context!.GetJsonAsync<MeetingGroup>($"/api/meeting-groups/{_proposalId}");
        return result.StatusCode == 200 && result.Body?.Name == name;
    }

    [Check("the proposer is an organizer of the group")]
    public async Task<bool> ProposerIsOrganizer()
    {
        var result = await Context!.GetJsonAsync<MeetingGroup>($"/api/meeting-groups/{_proposalId}");
        return result.Body?.Members.Any(m => m.MemberId == _userId && m.Role == "Organizer") == true;
    }

    [Check("the meeting {string} belongs to the group")]
    public async Task<bool> MeetingBelongsToGroup(string title)
    {
        var result = await Context!.GetJsonAsync<Meeting>($"/api/meetings/{_meetingId}");
        return result.StatusCode == 200
               && result.Body?.Title == title
               && result.Body.MeetingGroupId == _proposalId;
    }

    [Check("the meeting has {int} attendee")]
    public async Task<bool> MeetingHasNAttendees(int expected)
    {
        var result = await Context!.GetJsonAsync<Meeting>($"/api/meetings/{_meetingId}");
        return result.StatusCode == 200 && result.Body?.Attendees.Count == expected;
    }

    [Check("the meeting group {string} is listed")]
    public bool GroupIsListed(string name) => _meetingGroups.Any(g => g.Name == name);

    [Check("the meeting {string} is listed")]
    public bool MeetingIsListed(string title) => _meetings.Any(m => m.Title == title);

    /// <summary>
    /// Reads the subscription back rather than trusting the write's response — the Payments
    /// module is event-sourced, so this is the aggregate Marten rebuilt from the stream.
    /// </summary>
    [Check("the subscription is active")]
    public async Task<bool> SubscriptionIsActive()
    {
        var result = await Context!.GetJsonAsync<Subscription>($"/api/payments/subscriptions/{_subscriptionId}");
        return result.StatusCode == 200 && result.Body is { Status: SubscriptionStatus.Active };
    }

    [Check("the member's subscription expiration is in the future")]
    public async Task<bool> MemberSubscriptionIsCurrent()
    {
        var result = await Context!.GetJsonAsync<Member>($"/api/members/{_userId}");
        return result.Body?.SubscriptionExpirationDate > DateTime.UtcNow;
    }

    // ---- plumbing ---------------------------------------------------------------------------

    /// <summary>
    /// Run an HTTP call and wait for every message it cascades to be fully handled.
    ///
    /// The modules in this sample talk to each other over <c>UseDurableInbox()</c> local queues,
    /// so registering a user returns 200 *before* the Meetings module has created the Member,
    /// accepting a proposal returns before the group exists, and starting a subscription returns
    /// before the member's expiration date has moved. Asserting straight off the HTTP response
    /// would race the handler. See docs/sample-wiring.md footgun 7.
    ///
    /// Measured, not assumed: replacing this with a plain <c>await call()</c> fails 3 of the 12
    /// scenarios — every one that creates a meeting in a group the Meetings module has not yet
    /// been told about.
    /// </summary>
    private async Task<HttpResult<T>> awaitingCascades<T>(Func<Task<HttpResult<T>>> call)
    {
        var host = Context!.GetResource<IAlbaResource>().AlbaHost;
        HttpResult<T>? captured = null;

        // Explicitly typed: ExecuteAndWaitAsync overloads on Task and ValueTask, and an async
        // lambda is convertible to both.
        Func<IMessageContext, Task> act = async _ => { captured = await call(); };

        await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(act);

        return captured!;
    }
}
