using System.Net;

namespace Bobcat.Aspire.Samples;

/// <summary>
/// An HTTP vocabulary for specs over an Aspire application (bobcat#777): call a resource by the
/// name the AppHost gave it, assert on the response, and wait for work that finishes after the
/// request has returned. Plain step helpers, so each call renders as one step.
/// </summary>
// #region http-steps
public sealed class AspireHttpSteps(TestInfrastructure infrastructure)
{
    private HttpResponseMessage? _response;
    private string _body = "";

    [When("{method} {path} is sent to {resource}")]
    internal async Task IsSent(string method, string path, string resource)
    {
        using var client = infrastructure.CreateHttpClient(resource);
        _response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        _body = await _response.Content.ReadAsStringAsync();
    }

    [Then("the response status is {status}")]
    internal void TheStatusIs(int status)
        => SpecAssert.Check("status", (int)_response!.StatusCode, status);

    [Then("the response body is {body}")]
    internal void TheBodyIs(string body) => SpecAssert.Check("body", _body, body);

    /// <summary>
    /// Polls until the resource answers with the status or the timeout passes. Not a sleep: it
    /// returns the moment the system agrees, and the step fails with the last status it saw.
    /// </summary>
    [Then("{path} on {resource} eventually answers {status} within {seconds} seconds")]
    internal async Task EventuallyAnswers(string path, string resource, int status, int seconds)
    {
        using var client = infrastructure.CreateHttpClient(resource);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        HttpStatusCode last;
        do
        {
            last = (await client.GetAsync(path)).StatusCode;
            if ((int)last == status) break;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);

        SpecAssert.Check("status", (int)last, status);
    }
}
// #endregion http-steps
