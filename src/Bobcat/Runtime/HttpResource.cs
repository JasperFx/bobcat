namespace Bobcat.Runtime;

/// <summary>
/// One HTTP call a spec step wants carried to the application under test: the method, the full
/// URL (any grammar-level route prefix already applied), and an optional body to send as JSON —
/// serialized by the transport with the application's own JSON configuration, so a spec's wire
/// shape is the application's wire shape.
/// </summary>
public sealed record SpecHttpRequest(string Method, string Url, object? JsonBody = null);

/// <summary>
/// The response as the wire carried it. Status is never asserted by the transport itself — a 400
/// from a refused command is a result the spec asserts on (<c>Then the response is 400</c>), not
/// a transport failure.
/// </summary>
public sealed record SpecHttpResponse(int StatusCode, string Body, string? ContentType = null);

/// <summary>
/// A test resource that can carry an HTTP call to the application under test — the seam that lets
/// an HTTP-shaped grammar live in a package with no ASP.NET or Alba reference (issue #210,
/// following #211's delegate-shaped composition). <c>Bobcat.Alba</c>'s <c>AlbaResource</c> (both
/// forms) implements it over the in-memory TestServer; any other host resource can implement it
/// over whatever client reaches its application.
/// </summary>
/// <remarks>
/// Deliberately minimal — method, URL, JSON body, raw response. It exists so a grammar step can
/// drive a collapsed HTTP endpoint slice; a fixture that needs richer HTTP scenarios (multipart,
/// headers, redirects) should reach for the host's own client API (Alba scenarios) through a
/// hand-written step instead of growing this contract.
/// </remarks>
public interface IHttpResource : ITestResource
{
    /// <summary>Carry the call and return the response, whatever its status code.</summary>
    Task<SpecHttpResponse> SendAsync(SpecHttpRequest request, CancellationToken cancellation = default);
}
