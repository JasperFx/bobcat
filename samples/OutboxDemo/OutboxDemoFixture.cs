using Bobcat;
using Bobcat.Alba;
using Bobcat.Engine;

namespace OutboxDemo.Tests;

/// <summary>
/// Specs for the one endpoint this sample actually exposes: <c>POST /registration</c>, whose
/// happy path returns 204 while cascading a saga and two messages through Marten's outbox, and
/// whose <c>ValidateAsync</c> compound handler rejects a duplicate with a 409 ProblemDetails.
/// </summary>
/// <remarks>
/// The previous version of this file posted to <c>/api/meetings/member-joined</c> and
/// <c>/api/outbox/events</c> — endpoints belonging to a different sample entirely. It had never
/// been compiled, because the host project it sat in has no reference to Bobcat, so nothing
/// reported the drift.
/// </remarks>
[FixtureTitle("Outbox Demo")]
public class OutboxDemoFixture : Fixture
{
    private int _lastStatusCode;
    private string? _lastProblemDetail;

    public void BeforeEach()
    {
        _lastStatusCode = 0;
        _lastProblemDetail = null;
    }

    // One text per attribute: a step's text is matched against the attribute it is declared on,
    // so a Given and a When that read differently need a method each.
    [Given("a registration for member {string} at event {string}")]
    public Task GivenRegistration(IStepContext context, string memberId, string eventId)
        => submit(context, memberId, eventId, payment: 100m);

    [When("I submit a registration for member {string} at event {string}")]
    public Task SubmitRegistration(IStepContext context, string memberId, string eventId)
        => submit(context, memberId, eventId, payment: 100m);

    [When("I submit a registration for member {string} at event {string} paying {int}")]
    public Task SubmitRegistrationPaying(IStepContext context, string memberId, string eventId, int payment)
        => submit(context, memberId, eventId, payment);

    private async Task submit(IStepContext context, string memberId, string eventId, decimal payment)
    {
        var result = await context.PostJsonAsync<SubmitRegistration, ProblemDetailsBody>(
            "/registration",
            new SubmitRegistration(eventId, memberId, payment));

        _lastStatusCode = result.StatusCode;
        _lastProblemDetail = result.Body?.Detail;
    }

    [Check("the response status is {int}")]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Check("the rejection names the duplicate member {string}")]
    public bool RejectionNames(string memberId)
        => _lastProblemDetail?.Contains(memberId, StringComparison.Ordinal) == true;
}

/// <summary>
/// Just enough of RFC 7807 to read the rejection back. Declared here rather than reusing
/// <c>Microsoft.AspNetCore.Mvc.ProblemDetails</c> so the spec project does not take an MVC
/// dependency to read one string.
/// </summary>
public record ProblemDetailsBody(string? Detail, int? Status);
