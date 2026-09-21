using Alba;
using Bobcat.Engine;

namespace Bobcat.Alba;

/// <summary>One HTTP call as a step sees it: the status, and the body if there was one.</summary>
public record HttpResult<T>(int StatusCode, T? Body);

/// <summary>
/// The HTTP calls a fixture step makes against the application under test.
/// </summary>
/// <remarks>
/// Four shapes and one escape hatch, because that is what nine independently-written sample
/// suites converged on. The original package also carried raw-response helpers — headers, content
/// type, undecoded bytes — for specs that assert on the representation rather than a deserialized
/// body. Nothing needed them here, so they are not back; <see cref="SendAsync{T}"/> reaches the
/// whole Alba <c>Scenario</c> API for anything these four do not cover.
/// </remarks>
public static class AlbaStepContextExtensions
{
    public static Task<HttpResult<TResponse>> PostJsonAsync<TRequest, TResponse>(
        this IStepContext context, string url, TRequest body, string? resourceName = null)
        => context.SendAsync<TResponse>(s => s.Post.Json(body).ToUrl(url), resourceName);

    public static Task<HttpResult<TResponse>> PutJsonAsync<TRequest, TResponse>(
        this IStepContext context, string url, TRequest body, string? resourceName = null)
        => context.SendAsync<TResponse>(s => s.Put.Json(body).ToUrl(url), resourceName);

    public static Task<HttpResult<TResponse>> GetJsonAsync<TResponse>(
        this IStepContext context, string url, string? resourceName = null)
        => context.SendAsync<TResponse>(s => s.Get.Url(url), resourceName);

    public static Task<HttpResult<object>> DeleteAsync(
        this IStepContext context, string url, string? resourceName = null)
        => context.SendAsync<object>(s => s.Delete.Url(url), resourceName);

    /// <summary>
    /// Run any Alba scenario against the registered host. The status is surfaced rather than
    /// asserted — Alba checks for a 200 unless told otherwise, and a spec asserts whatever status
    /// it expects, so a 404 a scenario is testing for must not fail the call that produced it.
    /// </summary>
    public static async Task<HttpResult<T>> SendAsync<T>(
        this IStepContext context, Action<Scenario> configure, string? resourceName = null)
    {
        var result = await context.GetResource<IAlbaResource>(resourceName).AlbaHost.Scenario(scenario =>
        {
            configure(scenario);
            scenario.IgnoreStatusCode();
        });

        T? body = default;
        try { body = result.ReadAsJson<T>(); } catch { /* not every response carries JSON */ }

        return new HttpResult<T>(result.Context.Response.StatusCode, body);
    }
}
