using Bobcat;
using Bobcat.Alba;

namespace MoreSpeakers.Tests;

[FixtureTitle("More Speakers")]
public class MoreSpeakersFixture
{
    private Guid _speakerId;
    private Guid _mentorId;
    private Guid _mentorshipId;
    private int _lastStatusCode;
    private List<SpeakerDto> _speakers = [];

    [When("I register a speaker with email {string} and name {string}")]
    [Given("I register a speaker with email {string} and name {string}")]
    public async Task RegisterSpeaker(IStepContext context, string email, string name)
    {
        var result = await context.PostJsonAsync<RegisterSpeakerRequest, RegisterSpeakerResponse>(
            "/api/speakers",
            new RegisterSpeakerRequest(name, email));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null && _speakerId == Guid.Empty)
            _speakerId = result.Body.SpeakerId;
    }

    [Given("I register a mentor with email {string} and name {string}")]
    public async Task RegisterMentor(IStepContext context, string email, string name)
    {
        var result = await context.PostJsonAsync<RegisterSpeakerRequest, RegisterSpeakerResponse>(
            "/api/speakers",
            new RegisterSpeakerRequest(name, email));
        if (result.Body is not null)
            _mentorId = result.Body.SpeakerId;
    }

    [When("I get all speakers")]
    public async Task GetAllSpeakers(IStepContext context)
    {
        var result = await context.GetJsonAsync<List<SpeakerDto>>("/api/speakers");
        _lastStatusCode = result.StatusCode;
        _speakers = result.Body ?? [];
    }

    [When("I get speaker by id {string}")]
    public async Task GetSpeakerByStringId(IStepContext context, string id)
    {
        var result = await context.GetJsonAsync<SpeakerDto>($"/api/speakers/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I update the speaker bio to {string}")]
    public async Task UpdateSpeakerProfile(IStepContext context, string bio)
    {
        var result = await context.PostJsonAsync<UpdateSpeakerProfileRequest, object>(
            $"/api/speakers/{_speakerId}",
            new UpdateSpeakerProfileRequest(_speakerId, bio));
        _lastStatusCode = result.StatusCode;
    }

    [When("I request mentorship from the mentor")]
    [Given("I request mentorship from the mentor")]
    public async Task RequestMentorship(IStepContext context)
    {
        var result = await context.PostJsonAsync<RequestMentorshipRequest, RequestMentorshipResponse>(
            "/api/mentorships",
            new RequestMentorshipRequest(_speakerId, _mentorId));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _mentorshipId = result.Body.MentorshipId;
    }

    [When("I request mentorship from myself")]
    public async Task RequestSelfMentorship(IStepContext context)
    {
        var result = await context.PostJsonAsync<RequestMentorshipRequest, object>(
            "/api/mentorships",
            new RequestMentorshipRequest(_speakerId, _speakerId));
        _lastStatusCode = result.StatusCode;
    }

    [When("the mentor accepts the mentorship")]
    [Given("the mentor accepts the mentorship")]
    public async Task AcceptMentorship(IStepContext context)
    {
        var result = await context.PostJsonAsync<AcceptMentorshipRequest, object>(
            $"/api/mentorships/{_mentorshipId}/accept",
            new AcceptMentorshipRequest(_mentorshipId, _mentorId));
        _lastStatusCode = result.StatusCode;
    }

    [When("the mentor completes the mentorship")]
    public async Task CompleteMentorship(IStepContext context)
    {
        var result = await context.PostJsonAsync<CompleteMentorshipRequest, object>(
            $"/api/mentorships/{_mentorshipId}/complete",
            new CompleteMentorshipRequest(_mentorshipId, _mentorId));
        _lastStatusCode = result.StatusCode;
    }

    [Then("the response status is {int}")]
    [Check]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Then("the speaker id is returned")]
    [Check]
    public bool SpeakerIdReturned() => _speakerId != Guid.Empty;

    [Then("the mentorship id is returned")]
    [Check]
    public bool MentorshipIdReturned() => _mentorshipId != Guid.Empty;

    [Then("at least {int} speaker is returned")]
    [Check]
    public bool AtLeastNSpeakers(int min) => _speakers.Count >= min;
}

record RegisterSpeakerRequest(string Name, string Email);
record RegisterSpeakerResponse(Guid SpeakerId);
record UpdateSpeakerProfileRequest(Guid SpeakerId, string Bio);
record SpeakerDto(Guid Id, string Name, string Email, string? Bio);
record RequestMentorshipRequest(Guid MenteeId, Guid MentorId);
record RequestMentorshipResponse(Guid MentorshipId);
record AcceptMentorshipRequest(Guid MentorshipId, Guid MentorId);
record CompleteMentorshipRequest(Guid MentorshipId, Guid MentorId);
