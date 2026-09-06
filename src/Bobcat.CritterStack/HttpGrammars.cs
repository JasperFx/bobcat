using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Wolverine;
using Wolverine;

namespace Bobcat.CritterStack;

/// <summary>
/// The scenario's last HTTP call as the wire carried it — request and response — published onto
/// the scenario-state blackboard by <see cref="HttpGrammars"/>' act step and read by
/// <c>Then the response is …</c>. A separate capture from <see cref="TrackedExecution"/>,
/// deliberately: the tracked record is what the call <em>caused</em>, this is what the wire
/// <em>said</em>, and a refusal shows up in the second while leaving the first clean.
/// </summary>
public sealed record HttpExchange(SpecHttpRequest Request, SpecHttpResponse Response);

/// <summary>
/// The HTTP lane of the shipped Critter Stack vocabulary (issue #210): a grammar module whose act
/// drives the application over HTTP — the collapsed endpoint shape, where the endpoint <em>is</em>
/// the handler — inside Wolverine's tracked session, and records the same
/// <see cref="TrackedExecution"/> capture the command steps record. The store vocabulary's
/// assertions (<c>Then {event} is emitted</c> · <c>Then {message} is sent</c> · <c>Then the
/// {readmodel} read model contains</c> · <c>Then no events are emitted</c>) then assert on what
/// the call caused, unchanged — the two grammars cooperate only through the issue #212
/// scenario-state contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Steps:</b> <c>When {command} is posted to {string}</c> (one table row of body fields builds
/// the command record, sent as JSON) and <c>Then the response is {int}</c> (status assertion — an
/// HTTP guard refuses with ProblemDetails/400, not an exception, so the caught-exception
/// semantics of <c>Then validation fails with …</c> do not map; this is the refusal step for the
/// HTTP lane).
/// </para>
/// <para>
/// <b>Composition:</b> ride <see cref="CritterStackHttpFixture"/>, or compose directly with
/// <c>[IncludeGrammars(typeof(HttpGrammars), "/api/wallet")]</c> — the first constructor argument
/// is the route prefix applied to every posted route (issue #212 phase 2). The HTTP transport is
/// resolved from the suite's <see cref="IHttpResource"/> — <c>Bobcat.Alba</c>'s
/// <c>AlbaResource</c> implements it — so this package still references no Alba and no ASP.NET.
/// </para>
/// </remarks>
public class HttpGrammars : Fixture
{
    private readonly string _routePrefix;
    private readonly string? _hostResource;
    private readonly string? _storeName;
    private readonly int _timeoutInMilliseconds;

    public HttpGrammars(string routePrefix = "", string? hostResource = null, string? storeName = null,
        int timeoutInMilliseconds = 5000)
    {
        _routePrefix = routePrefix;
        _hostResource = hostResource;
        _storeName = storeName;
        _timeoutInMilliseconds = timeoutInMilliseconds;
    }

    private IStepContext Ctx => Context ?? throw new InvalidOperationException(
        "No IStepContext is set on the grammar module — an HTTP grammar step ran outside a scenario.");

    /// <summary>
    /// The act: build the command record from the table row, POST it as JSON to the (prefixed)
    /// route through the suite's <see cref="IHttpResource"/>, inside Wolverine's tracked session —
    /// so the step returns only when everything the call caused (cascades, local queues drained)
    /// has landed. The outcome is captured, never thrown: the wire's verdict lands in
    /// <see cref="HttpExchange"/> for <c>Then the response is …</c>, and what the call caused
    /// lands in <see cref="TrackedExecution"/> for the store vocabulary.
    /// </summary>
    [When("{command} is posted to {string}")]
    public async Task WhenCommandIsPosted(Type command, string route, StepTable? fields)
    {
        if (fields is { Rows.Count: > 1 })
            throw new SpecCriticalException(
                $"'When {command.Name} is posted to …' expects at most one table row of body fields, but got {fields.Rows.Count}.");

        var body = fields is { Rows.Count: 1 }
            ? RecordBuilding.Build(command, fields.AsDictionaries()[0])
            : RecordBuilding.Build(command, new Dictionary<string, string>());

        Ctx.RecordTouchedType(command);

        var request = new SpecHttpRequest("POST", _routePrefix + route, body);
        var transport = resolveTransport();

        SpecHttpResponse? response = null;
        await TrackedActs.ExecuteAsync(
            Ctx,
            () => Ctx.TrackActivity(_hostResource)
                .Timeout(TimeSpan.FromMilliseconds(_timeoutInMilliseconds))
                .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
                    response = await transport.SendAsync(request, Ctx.Cancellation))),
            hostResource: _hostResource,
            storeName: _storeName);

        if (response != null) Ctx.SetState(new HttpExchange(request, response));
    }

    /// <summary>
    /// Assert the status code of the scenario's last HTTP call. This is the HTTP lane's refusal
    /// vocabulary: a guard that refuses with ProblemDetails/400 threw nothing, so
    /// <c>Then validation fails with …</c> (caught-exception semantics) cannot describe it —
    /// <c>Then the response is 400</c> composed with <c>Then no events are emitted</c> does.
    /// </summary>
    [Then("the response is {int}")]
    public void ThenTheResponseIs(int statusCode)
    {
        if (!Ctx.TryGetState<HttpExchange>(out var exchange))
            throw new SpecAssertionException(
                "Expected an HTTP response to assert on, but no '… is posted to …' step has run in this scenario.");

        if (exchange.Response.StatusCode != statusCode)
            throw new SpecAssertionException(
                $"Expected HTTP {statusCode}, but the {exchange.Request.Method} to '{exchange.Request.Url}' " +
                $"returned {exchange.Response.StatusCode}.{describeBody(exchange.Response)}");
    }

    private IHttpResource resolveTransport()
    {
        try
        {
            return Ctx.GetResource<IHttpResource>(_hostResource);
        }
        catch (InvalidOperationException e)
        {
            throw new SpecCriticalException(
                "The HTTP grammar needs a registered test resource implementing " +
                "Bobcat.Runtime.IHttpResource to carry the call — Bobcat.Alba's AlbaResource " +
                "does. " + e.Message, e);
        }
    }

    private static string describeBody(SpecHttpResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Body)) return "";
        var body = response.Body.Length > 500 ? response.Body[..500] + "…" : response.Body;
        return $" Body: {body}";
    }
}
