using Alba;
using Bobcat;
using Bobcat.Runtime;
using Marten;
using Shouldly;

namespace OutboxDemo.Tests;

[FixtureTitle("Outbox Demo Registration")]
public class RegistrationFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;
    private string? _lastMemberId;
    private string? _lastEventId;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _lastStatusCode = 0;
        _lastMemberId = null;
        _lastEventId = null;
        return Task.CompletedTask;
    }

    [Given("a registration for event {string} member {string} with payment {int} exists")]
    public async Task CreateRegistrationGiven(string eventId, string memberId, int payment)
    {
        await _host.Scenario(x =>
        {
            x.Post.Json(new SubmitRegistration(eventId, memberId, payment)).ToUrl("/registration");
            x.StatusCodeShouldBe(204);
        });
    }

    [When("I submit a registration for event {string} member {string} with payment {int}")]
    public async Task SubmitRegistration(string eventId, string memberId, int payment)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new SubmitRegistration(eventId, memberId, payment)).ToUrl("/registration");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        _lastMemberId = memberId;
        _lastEventId = eventId;
    }

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected) => _lastStatusCode.ShouldBe(expected);

    [Then("the registration should be persisted in the database")]
    public async Task RegistrationShouldBePersisted()
    {
        using var session = _host.DocumentStore().LightweightSession();
        var registrations = await session.Query<Registration>()
            .Where(r => r.MemberId == _lastMemberId && r.EventId == _lastEventId)
            .ToListAsync();
        registrations.ShouldHaveSingleItem();
    }

    [Then("the registration member id should be {string}")]
    public async Task RegistrationMemberIdShouldBe(string memberId)
    {
        using var session = _host.DocumentStore().LightweightSession();
        var loaded = await session.Query<Registration>()
            .FirstOrDefaultAsync(r => r.MemberId == memberId && r.EventId == _lastEventId);
        loaded.ShouldNotBeNull();
        loaded!.MemberId.ShouldBe(memberId);
    }

    [Then("the registration event id should be {string}")]
    public async Task RegistrationEventIdShouldBe(string eventId)
    {
        using var session = _host.DocumentStore().LightweightSession();
        var loaded = await session.Query<Registration>()
            .FirstOrDefaultAsync(r => r.MemberId == _lastMemberId && r.EventId == eventId);
        loaded.ShouldNotBeNull();
        loaded!.EventId.ShouldBe(eventId);
    }
}
