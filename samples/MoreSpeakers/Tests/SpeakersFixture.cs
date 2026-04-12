using Alba;
using Bobcat;
using Bobcat.Runtime;
using Marten;
using Mentorships;
using Shouldly;
using Speakers;

namespace MoreSpeakers.Tests;

[FixtureTitle("More Speakers")]
public class SpeakersFixture : Fixture
{
    private IAlbaHost _host = null!;

    private Speaker? _speaker;
    private Speaker? _updatedSpeaker;
    private Speaker[]? _allSpeakers;
    private Mentorship? _mentorship;
    private Speaker? _mentor;
    private Speaker? _mentee;
    private int _lastStatusCode;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _speaker = null;
        _updatedSpeaker = null;
        _allSpeakers = null;
        _mentorship = null;
        _mentor = null;
        _mentee = null;
        _lastStatusCode = 0;
        return Task.CompletedTask;
    }

    private async Task<Speaker> StoreSpeakerDirect(string firstName, string lastName, string email,
        SpeakerType type = SpeakerType.New, bool availableForMentoring = false, int maxMentees = 0)
    {
        await using var session = _host.DocumentStore().LightweightSession();
        var s = new Speaker
        {
            FirstName = firstName, LastName = lastName, Email = email,
            Type = type, IsAvailableForMentoring = availableForMentoring, MaxMentees = maxMentees
        };
        session.Store(s);
        await session.SaveChangesAsync();
        return s;
    }

    private async Task<Mentorship> StoreMentorshipDirect(MentorshipStatus status)
    {
        await using var session = _host.DocumentStore().LightweightSession();
        var m = new Mentorship
        {
            Status = status, RequestedAt = DateTimeOffset.UtcNow,
            StartedAt = status == MentorshipStatus.Active ? DateTimeOffset.UtcNow : null
        };
        session.Store(m);
        await session.SaveChangesAsync();
        return m;
    }

    // ── Given ────────────────────────────────────────────────────────────────

    [Given("a speaker with email {string} is registered")]
    public async Task RegisterSpeakerGiven(string email)
    {
        await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterSpeaker(email, "First", "Speaker", SpeakerType.New)).ToUrl("/api/speakers");
            x.StatusCodeShouldBeOk();
        });
    }

    [Given("a speaker named {string} with email {string} exists in the database")]
    public async Task StoreSpeakerGiven(string name, string email)
    {
        var parts = name.Split(' ');
        var firstName = parts[0];
        var lastName = parts.Length > 1 ? parts[1] : "Test";
        _speaker = await StoreSpeakerDirect(firstName, lastName, email);
    }

    [Given("a mentor and mentee exist")]
    public async Task SeedMentorAndMentee()
    {
        _mentor = await StoreSpeakerDirect("Mentor", "Speaker", "mentor@test.com",
            SpeakerType.Experienced, availableForMentoring: true, maxMentees: 5);
        _mentee = await StoreSpeakerDirect("Mentee", "Speaker", "mentee@test.com", SpeakerType.New);
    }

    [Given("a pending mentorship exists")]
    public async Task SeedPendingMentorship()
    {
        _mentor = await StoreSpeakerDirect("Mentor2", "Speaker", "mentor2@test.com",
            SpeakerType.Experienced, availableForMentoring: true, maxMentees: 5);
        _mentee = await StoreSpeakerDirect("Mentee2", "Speaker", "mentee2@test.com", SpeakerType.New);

        await using var session = _host.DocumentStore().LightweightSession();
        _mentorship = new Mentorship
        {
            MentorId = _mentor.Id, MentorName = _mentor.FullName,
            MenteeId = _mentee.Id, MenteeName = _mentee.FullName,
            Status = MentorshipStatus.Pending, RequestedAt = DateTimeOffset.UtcNow
        };
        session.Store(_mentorship);
        await session.SaveChangesAsync();
    }

    [Given("an active mentorship exists")]
    public async Task SeedActiveMentorship()
    {
        _mentorship = await StoreMentorshipDirect(MentorshipStatus.Active);
    }

    // ── When ─────────────────────────────────────────────────────────────────

    [When("I register a speaker with email {string} first name {string} and last name {string}")]
    public async Task RegisterSpeaker(string email, string firstName, string lastName)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterSpeaker(email, firstName, lastName, SpeakerType.New)).ToUrl("/api/speakers");
            x.StatusCodeShouldBeOk();
        });
        _speaker = result.ReadAsJson<Speaker>()!;
        _lastStatusCode = 200;
    }

    [When("I try to register a speaker with email {string}")]
    public async Task TryRegisterDuplicate(string email)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterSpeaker(email, "Second", "Speaker", SpeakerType.New)).ToUrl("/api/speakers");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I get all speakers")]
    public async Task GetAllSpeakers()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/api/speakers");
            x.StatusCodeShouldBeOk();
        });
        _allSpeakers = result.ReadAsJson<Speaker[]>()!;
    }

    [When("I get a speaker by a random id")]
    public async Task GetSpeakerByRandomId()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/api/speakers/{Guid.NewGuid()}");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I update the speaker first name to {string} with {int} max mentees and expertise {string}")]
    public async Task UpdateSpeaker(string firstName, int maxMentees, string expertise)
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateSpeakerProfile(
                _speaker!.Id, firstName, "Name", "New bio", null, null, null,
                true, maxMentees, "Public speaking", [expertise], null
            )).ToUrl($"/api/speakers/{_speaker.Id}");
            x.StatusCodeShouldBeOk();
        });
        _updatedSpeaker = result.ReadAsJson<Speaker>()!;
        _lastStatusCode = 200;
    }

    [When("I request mentorship from the mentor")]
    public async Task RequestMentorship()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RequestMentorship(
                _mentor!.Id, _mentee!.Id, MentorshipType.NewToExperienced,
                "Help me", ["Public Speaking"], "Weekly"
            )).ToUrl("/api/mentorships");
            x.StatusCodeShouldBeOk();
        });
        _mentorship = result.ReadAsJson<Mentorship>()!;
        _lastStatusCode = 200;
    }

    [When("I try to request mentorship where mentor and mentee are the same")]
    public async Task TryRequestSelfMentorship()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RequestMentorship(
                _mentor!.Id, _mentor.Id, MentorshipType.ExperiencedToExperienced,
                null, null, null
            )).ToUrl("/api/mentorships");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I accept the mentorship with message {string}")]
    public async Task AcceptMentorship(string message)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new AcceptMentorship(_mentorship!.Id, message))
                .ToUrl($"/api/mentorships/{_mentorship.Id}/accept");
            x.StatusCodeShouldBeOk();
        });
        _mentorship = result.ReadAsJson<Mentorship>()!;
        _lastStatusCode = 200;
    }

    [When("I try to accept the mentorship")]
    public async Task TryAcceptNonPendingMentorship()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new AcceptMentorship(_mentorship!.Id, null))
                .ToUrl($"/api/mentorships/{_mentorship.Id}/accept");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I complete the mentorship")]
    public async Task CompleteMentorship()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CompleteMentorship(_mentorship!.Id))
                .ToUrl($"/api/mentorships/{_mentorship.Id}/complete");
            x.StatusCodeShouldBeOk();
        });
        _mentorship = result.ReadAsJson<Mentorship>()!;
        _lastStatusCode = 200;
    }

    // ── Then / Check ─────────────────────────────────────────────────────────

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected) => _lastStatusCode.ShouldBe(expected);

    [Then("the speaker first name should be {string}")]
    public void SpeakerFirstNameShouldBe(string expected) => _speaker!.FirstName.ShouldBe(expected);

    [Then("the speaker email should be {string}")]
    public void SpeakerEmailShouldBe(string expected) => _speaker!.Email.ShouldBe(expected);

    [Then("there should be at least {int} speaker")]
    public void SpeakerCountAtLeast(int min) => (_allSpeakers!.Length >= min).ShouldBeTrue();

    [Then("the updated speaker first name should be {string}")]
    public void UpdatedSpeakerFirstNameShouldBe(string expected) => _updatedSpeaker!.FirstName.ShouldBe(expected);

    [Check("the speaker should be available for mentoring")]
    public bool SpeakerAvailableForMentoring() => _updatedSpeaker?.IsAvailableForMentoring == true;

    [Then("the speaker max mentees should be {int}")]
    public void SpeakerMaxMenteesShouldBe(int expected) => _updatedSpeaker!.MaxMentees.ShouldBe(expected);

    [Then("the speaker expertise should contain {string}")]
    public void SpeakerExpertiseContains(string expected) => _updatedSpeaker!.Expertise.ShouldContain(expected);

    [Then("the mentorship status should be {string}")]
    public void MentorshipStatusShouldBe(string expected) =>
        _mentorship!.Status.ToString().ShouldBe(expected);

    [Check("the mentorship mentor id should match")]
    public bool MentorshipMentorIdMatches() => _mentorship?.MentorId == _mentor?.Id;

    [Then("the mentorship response should be {string}")]
    public void MentorshipResponseShouldBe(string expected) => _mentorship!.ResponseMessage.ShouldBe(expected);

    [Check("the mentorship completed at should be set")]
    public bool MentorshipCompletedAtIsSet() => _mentorship?.CompletedAt != null;
}
