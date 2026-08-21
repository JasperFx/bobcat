using Bobcat;
using Bobcat.Alba;
using Mentorships;
using Speakers;

namespace MoreSpeakers.Tests;

/// <summary>
/// Specs for the MoreSpeakers document-database sample. Reuses the host's own command and
/// document types via the project reference rather than re-declaring request/response records
/// locally — the file this replaced posted a <c>RegisterSpeakerRequest(Name, Email)</c> to an
/// endpoint whose command is <c>RegisterSpeaker(Email, FirstName, LastName, Type, …)</c>,
/// expected a <c>SpeakerId</c> from a body whose id property is <c>Id</c>, POSTed a two-field
/// profile update to a <c>[WolverinePut]</c> whose validator requires first and last name, and
/// registered "mentors" the host would refuse to mentor because nothing ever flagged them as
/// available. Nothing had ever compiled it, so nothing reported any of that.
/// </summary>
[FixtureTitle("More Speakers")]
public class MoreSpeakersFixture : Fixture
{
    private Speaker? _speaker;
    private Guid _speakerId;
    private Guid _mentorId;
    private Guid _mentorshipId;
    private int _lastStatusCode;
    private List<Speaker> _speakers = [];

    public Task BeforeEach()
    {
        _speaker = null;
        _speakerId = Guid.Empty;
        _mentorId = Guid.Empty;
        _mentorshipId = Guid.Empty;
        _lastStatusCode = 0;
        _speakers = [];
        return Task.CompletedTask;
    }

    // ---- speakers -------------------------------------------------------------------------

    [Given("I register a speaker with email {string} and name {string}")]
    public Task GivenRegisterSpeaker(string email, string name) => registerSpeakerCore(email, name);

    [When("I register a speaker with email {string} and name {string}")]
    public Task WhenRegisterSpeaker(string email, string name) => registerSpeakerCore(email, name);

    private async Task registerSpeakerCore(string email, string name)
    {
        var (first, last) = splitName(name);
        var result = await Context!.PostJsonAsync<RegisterSpeaker, Speaker>(
            "/api/speakers", new RegisterSpeaker(email, first, last, SpeakerType.New));

        _lastStatusCode = result.StatusCode;

        // The first speaker a scenario registers is "the speaker" every later step refers to;
        // the duplicate-email scenario's second call is a 409 with a ProblemDetails body that
        // must not displace it.
        if (result.StatusCode == 201 && result.Body is not null && _speakerId == Guid.Empty)
        {
            _speaker = result.Body;
            _speakerId = result.Body.Id;
        }
    }

    /// <summary>
    /// A mentor is a speaker the host will let others request mentorship from, and
    /// <c>RequestMentorshipEndpoint.Validate</c> refuses with a 400 unless
    /// <c>IsAvailableForMentoring</c> is set. Registration has no such flag — it is a profile
    /// attribute — so registering a mentor is a POST followed by the PUT that opts them in,
    /// as an experienced speaker. Two calls against the real API rather than a host change
    /// that would let registration say something a new speaker cannot.
    /// </summary>
    [Given("I register a mentor with email {string} and name {string}")]
    public async Task RegisterMentor(string email, string name)
    {
        var (first, last) = splitName(name);
        var registered = await Context!.PostJsonAsync<RegisterSpeaker, Speaker>(
            "/api/speakers", new RegisterSpeaker(email, first, last, SpeakerType.Experienced));

        _lastStatusCode = registered.StatusCode;
        if (registered.Body is null) return;

        var mentor = registered.Body;
        var optIn = await Context!.PutJsonAsync<UpdateSpeakerProfile, Speaker>(
            $"/api/speakers/{mentor.Id}",
            profileFor(mentor) with { IsAvailableForMentoring = true, MaxMentees = 3 });

        _lastStatusCode = optIn.StatusCode;
        _mentorId = mentor.Id;
    }

    /// <summary>
    /// The counter-case to the step above: a speaker who exists but never opted in, so a
    /// mentorship request naming them must be refused.
    /// </summary>
    [Given("I register a speaker who is not mentoring with email {string} and name {string}")]
    public async Task RegisterNonMentor(string email, string name)
    {
        var (first, last) = splitName(name);
        var result = await Context!.PostJsonAsync<RegisterSpeaker, Speaker>(
            "/api/speakers", new RegisterSpeaker(email, first, last, SpeakerType.Experienced));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _mentorId = result.Body.Id;
    }

    [When("I get all speakers")]
    public async Task GetAllSpeakers()
    {
        var result = await Context!.GetJsonAsync<List<Speaker>>("/api/speakers");
        _lastStatusCode = result.StatusCode;
        _speakers = result.Body ?? [];
    }

    [When("I get speaker by id {string}")]
    public async Task GetSpeakerByStringId(string id)
    {
        var result = await Context!.GetJsonAsync<Speaker>($"/api/speakers/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I update the speaker bio to {string}")]
    public async Task UpdateSpeakerBio(string bio)
    {
        // PUT, not POST — the endpoint is a [WolverinePut] — and the command is the whole
        // profile, whose validator requires first and last name, so the fixture replays the
        // speaker it registered with the one field changed.
        var result = await Context!.PutJsonAsync<UpdateSpeakerProfile, Speaker>(
            $"/api/speakers/{_speakerId}", profileFor(_speaker!) with { Bio = bio });

        _lastStatusCode = result.StatusCode;
    }

    // ---- mentorships ----------------------------------------------------------------------

    [Given("I request mentorship from the mentor")]
    public Task GivenRequestMentorship() => requestMentorshipCore(_mentorId);

    [When("I request mentorship from the mentor")]
    public Task WhenRequestMentorship() => requestMentorshipCore(_mentorId);

    [When("I request mentorship from myself")]
    public Task RequestSelfMentorship() => requestMentorshipCore(_speakerId);

    private async Task requestMentorshipCore(Guid mentorId)
    {
        var result = await Context!.PostJsonAsync<RequestMentorship, Mentorship>(
            "/api/mentorships",
            new RequestMentorship(mentorId, _speakerId, MentorshipType.NewToExperienced,
                "Help me with my first talk", ["Distributed systems"], "Weekly"));

        _lastStatusCode = result.StatusCode;
        if (result.StatusCode == 201 && result.Body is not null) _mentorshipId = result.Body.Id;
    }

    [Given("the mentor accepts the mentorship")]
    public Task GivenAcceptMentorship() => acceptMentorshipCore();

    [When("the mentor accepts the mentorship")]
    public Task WhenAcceptMentorship() => acceptMentorshipCore();

    private async Task acceptMentorshipCore()
    {
        var result = await Context!.PostJsonAsync<AcceptMentorship, Mentorship>(
            $"/api/mentorships/{_mentorshipId}/accept",
            new AcceptMentorship(_mentorshipId, "Happy to help"));

        _lastStatusCode = result.StatusCode;
    }

    [When("the mentor completes the mentorship")]
    public async Task CompleteMentorship()
    {
        var result = await Context!.PostJsonAsync<CompleteMentorship, Mentorship>(
            $"/api/mentorships/{_mentorshipId}/complete",
            new CompleteMentorship(_mentorshipId));

        _lastStatusCode = result.StatusCode;
    }

    // ---- assertions -----------------------------------------------------------------------

    [Check("the response status is {int}")]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Check("the speaker id is returned")]
    public bool SpeakerIdReturned() => _speakerId != Guid.Empty;

    [Check("the mentorship id is returned")]
    public bool MentorshipIdReturned() => _mentorshipId != Guid.Empty;

    [Check("at least {int} speaker is returned")]
    public bool AtLeastNSpeakers(int min) => _speakers.Count >= min;

    [Check("the speaker list contains {string}")]
    public bool SpeakerListContains(string email) => _speakers.Any(s => s.Email == email);

    /// <summary>
    /// Reads the speaker back rather than trusting the write's response body — the POST
    /// echoes what it was about to store, and a sample whose point is "Marten stores the
    /// document" should show the document came back out.
    /// </summary>
    [Check("the stored speaker is named {string} with email {string}")]
    public async Task<bool> StoredSpeakerIs(string name, string email)
    {
        var result = await Context!.GetJsonAsync<Speaker>($"/api/speakers/{_speakerId}");
        return result.Body is not null && result.Body.FullName == name && result.Body.Email == email;
    }

    [Check("the stored speaker bio is {string}")]
    public async Task<bool> StoredSpeakerBioIs(string bio)
    {
        var result = await Context!.GetJsonAsync<Speaker>($"/api/speakers/{_speakerId}");
        return result.Body is not null && result.Body.Bio == bio;
    }

    [Check("the stored mentorship status is {string}")]
    public async Task<bool> StoredMentorshipStatusIs(string status)
    {
        var result = await Context!.GetJsonAsync<Mentorship>($"/api/mentorships/{_mentorshipId}");
        return result.Body is not null && result.Body.Status.ToString() == status;
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>
    /// The feature names speakers as one string ("Alice Speaker"); the host's command wants
    /// first and last separately. First word is the first name, the rest is the last name.
    /// </summary>
    private static (string First, string Last) splitName(string name)
    {
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (name, name);
    }

    /// <summary>
    /// The full-profile PUT command for a speaker as currently stored, so a step that wants to
    /// change one field can <c>with</c> it without re-stating everything the validator requires.
    /// </summary>
    private static UpdateSpeakerProfile profileFor(Speaker speaker) => new(
        speaker.Id,
        speaker.FirstName,
        speaker.LastName,
        speaker.Bio,
        speaker.Goals,
        speaker.HeadshotUrl,
        speaker.SessionizeUrl,
        speaker.IsAvailableForMentoring,
        speaker.MaxMentees,
        speaker.MentorshipFocus,
        speaker.Expertise,
        speaker.SocialLinks);
}
