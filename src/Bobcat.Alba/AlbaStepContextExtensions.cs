using System.Text;
using System.Text.Json;
using Alba;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat.Alba;

/// <summary>
/// Marker interface so extension methods can locate an Alba host without knowing the concrete type.
/// </summary>
public interface IAlbaResource : ITestResource
{
    IAlbaHost AlbaHost { get; }
}

/// <summary>
/// Simple result carrier for Alba HTTP calls made from step methods.
/// </summary>
public record HttpResult<T>(int StatusCode, T? Body);

/// <summary>
/// An HTTP response as the wire carried it — status, content type, headers, raw bytes — for steps
/// that assert on the representation itself rather than on a deserialized body: an export
/// endpoint's CTRF JSON or JUnit XML, an NDJSON stream, a CSV download, a problem-details payload
/// whose shape is the thing under test. The typed helpers (<c>GetJsonAsync</c> and friends) stay
/// the default; this is the shape they lacked, so a fixture no longer has to drop to
/// <c>IAlbaHost.Scenario</c> just to see a content type.
/// </summary>
public sealed class RawResponse
{
    public RawResponse(int statusCode, string? contentType, IReadOnlyDictionary<string, string[]> headers, byte[] bytes)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Headers = headers;
        Bytes = bytes;
    }

    public int StatusCode { get; }

    /// <summary>The full <c>Content-Type</c> header as sent, e.g. <c>application/json; charset=utf-8</c>, or null.</summary>
    public string? ContentType { get; }

    /// <summary>The media type alone — <see cref="ContentType"/> without its parameters, e.g. <c>application/json</c>.</summary>
    public string? MediaType => ContentType?.Split(';')[0].Trim() is { Length: > 0 } media ? media : null;

    /// <summary>Every response header, multi-valued.</summary>
    public IReadOnlyDictionary<string, string[]> Headers { get; }

    /// <summary>The response body as received.</summary>
    public byte[] Bytes { get; }

    /// <summary>The response body decoded as UTF-8. Empty string for an empty body.</summary>
    public string Body => Encoding.UTF8.GetString(Bytes);

    /// <summary>
    /// Deserialize the body as JSON — web defaults (camelCase, case-insensitive) unless options are
    /// given. Throws if the body is not JSON; this is the explicit ask, unlike the typed helpers'
    /// best-effort body.
    /// </summary>
    public T? ReadAsJson<T>(JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize<T>(Bytes, options ?? webJson);

    private static readonly JsonSerializerOptions webJson = new(JsonSerializerDefaults.Web);

    /// <summary>Captures the response of a completed Alba scenario.</summary>
    public static async Task<RawResponse> From(IScenarioResult result)
    {
        var response = result.Context.Response;
        var headers = response.Headers.ToDictionary(
            h => h.Key, h => h.Value.Where(v => v != null).Select(v => v!).ToArray(), StringComparer.OrdinalIgnoreCase);

        using var memory = new MemoryStream();
        var body = response.Body;
        if (body.CanSeek) body.Position = 0;
        await body.CopyToAsync(memory);

        return new RawResponse(response.StatusCode, response.ContentType, headers, memory.ToArray());
    }
}

/// <summary>
/// IStepContext extension methods that delegate to the registered IAlbaResource.
/// Fixture steps can call context.PostJsonAsync / GetJsonAsync / DeleteAsync
/// without holding a direct reference to IAlbaHost.
/// </summary>
public static class AlbaStepContextExtensions
{
    public static async Task<HttpResult<TResponse>> PostJsonAsync<TRequest, TResponse>(
        this IStepContext context, string url, TRequest body, string? resourceName = null)
    {
        var host = context.GetResource<IAlbaResource>(resourceName).AlbaHost;
        IScenarioResult result = await host.Scenario(s =>
        {
            s.Post.Json(body).ToUrl(url);
            // Suppress Alba's implicit StatusCodeShouldBeOk() assertion. We
            // surface the status code on HttpResult so step assertions can
            // verify whatever the spec actually expects (201 / 204 / 404 /
            // anything else) instead of failing scenarios that intentionally
            // exercise non-200 paths.
            s.IgnoreStatusCode();
        });
        var statusCode = result.Context.Response.StatusCode;
        TResponse? responseBody = default;
        try { responseBody = result.ReadAsJson<TResponse>(); } catch { }
        return new HttpResult<TResponse>(statusCode, responseBody);
    }

    public static async Task<HttpResult<TResponse>> PutJsonAsync<TRequest, TResponse>(
        this IStepContext context, string url, TRequest body, string? resourceName = null)
    {
        var host = context.GetResource<IAlbaResource>(resourceName).AlbaHost;
        IScenarioResult result = await host.Scenario(s =>
        {
            s.Put.Json(body).ToUrl(url);
            s.IgnoreStatusCode();
        });
        var statusCode = result.Context.Response.StatusCode;
        TResponse? responseBody = default;
        try { responseBody = result.ReadAsJson<TResponse>(); } catch { }
        return new HttpResult<TResponse>(statusCode, responseBody);
    }

    public static async Task<HttpResult<TResponse>> GetJsonAsync<TResponse>(
        this IStepContext context, string url, string? resourceName = null)
    {
        var host = context.GetResource<IAlbaResource>(resourceName).AlbaHost;
        IScenarioResult result = await host.Scenario(s =>
        {
            s.Get.Url(url);
            s.IgnoreStatusCode();
        });
        var statusCode = result.Context.Response.StatusCode;
        TResponse? body = default;
        try { body = result.ReadAsJson<TResponse>(); } catch { }
        return new HttpResult<TResponse>(statusCode, body);
    }

    public static async Task<HttpResult<object>> DeleteAsync(
        this IStepContext context, string url, string? resourceName = null)
    {
        var host = context.GetResource<IAlbaResource>(resourceName).AlbaHost;
        IScenarioResult result = await host.Scenario(s =>
        {
            s.Delete.Url(url);
            s.IgnoreStatusCode();
        });
        return new HttpResult<object>(result.Context.Response.StatusCode, null);
    }

    /// <summary>
    /// GET a url and return the response as the wire carried it — status, content type, headers
    /// and raw body — for assertions on the representation itself (an export's JSON/XML/NDJSON,
    /// a download, a problem-details payload). Never throws on a non-200 status.
    /// </summary>
    public static Task<RawResponse> GetRawAsync(this IStepContext context, string url, string? resourceName = null)
        => context.SendRawAsync(s => s.Get.Url(url), resourceName);

    /// <summary>
    /// POST a raw body with an explicit content type and return the raw response. For the request
    /// shapes the JSON helpers cannot express — NDJSON, XML, form posts, a CSV upload.
    /// </summary>
    public static Task<RawResponse> PostRawAsync(this IStepContext context, string url, string body,
        string contentType, string? resourceName = null)
        => context.SendRawAsync(s => s.Post.Text(body).ToUrl(url).ContentType(contentType), resourceName);

    /// <summary>
    /// Run any Alba scenario against the registered host and return the raw response. The escape
    /// hatch for everything the shaped helpers do not cover — headers, query strings, any verb —
    /// without leaving the helper family: the status code is surfaced rather than asserted (Alba's
    /// implicit 200 check is suppressed), so a step asserts whatever the spec expects.
    /// </summary>
    public static async Task<RawResponse> SendRawAsync(this IStepContext context, Action<Scenario> configure,
        string? resourceName = null)
    {
        var host = context.GetResource<IAlbaResource>(resourceName).AlbaHost;
        IScenarioResult result = await host.Scenario(s =>
        {
            configure(s);
            s.IgnoreStatusCode();
        });
        return await RawResponse.From(result);
    }
}
