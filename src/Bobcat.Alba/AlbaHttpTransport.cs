using Alba;
using Bobcat.Alba;

namespace Bobcat.Runtime;

/// <summary>
/// The <see cref="IHttpResource"/> implementation both <see cref="AlbaResource"/> forms share:
/// one <see cref="SpecHttpRequest"/> carried as an Alba scenario against the in-memory
/// TestServer. The JSON body goes through Alba's own serialization, which uses the hosted
/// application's configured JSON options — a spec's wire shape is the application's wire shape.
/// Status codes are never asserted here (<c>IgnoreStatusCode</c>): a 400 from a refused command
/// is a result for the spec to assert on, not a transport failure.
/// </summary>
internal static class AlbaHttpTransport
{
    public static async Task<SpecHttpResponse> SendAsync(IAlbaHost host, SpecHttpRequest request)
    {
        var result = await host.Scenario(s =>
        {
            var method = request.Method.ToUpperInvariant();
            switch (method)
            {
                case "GET":
                    s.Get.Url(request.Url);
                    break;
                case "DELETE":
                    s.Delete.Url(request.Url);
                    break;
                case "POST" when request.JsonBody != null:
                    s.Post.Json(request.JsonBody).ToUrl(request.Url);
                    break;
                case "POST":
                    s.Post.Url(request.Url);
                    break;
                case "PUT" when request.JsonBody != null:
                    s.Put.Json(request.JsonBody).ToUrl(request.Url);
                    break;
                case "PUT":
                    s.Put.Url(request.Url);
                    break;
                default:
                    throw new NotSupportedException(
                        $"HTTP method '{request.Method}' is not supported by the Alba spec transport. " +
                        "Use GET, POST, PUT or DELETE, or drive the call through an Alba scenario in a fixture step.");
            }

            s.IgnoreStatusCode();
        });

        var raw = await RawResponse.From(result);
        return new SpecHttpResponse(raw.StatusCode, raw.Body, raw.ContentType);
    }
}
