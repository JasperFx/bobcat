using Alba;
using Bobcat.Engine;

namespace EcommerceModularMonolith.Tests;

/// <summary>One HTTP call as a step sees it: the status, and the body if there was one.</summary>
public record HttpResult<T>(int StatusCode, T? Body);

/// <summary>
/// The four shapes this sample's fixtures need from Alba, over the raw <c>IAlbaHost</c>.
/// </summary>
/// <remarks>
/// <b>Duplicated in every sample, deliberately.</b> Bobcat ships no Alba integration, so a sample
/// that wants one writes it. Nine identical copies is not an oversight — it is the measurement of
/// what the deleted <c>Bobcat.Alba</c> was actually carrying.
/// </remarks>
public static class AlbaSupport
{
    public static Task<HttpResult<TResponse>> PostJsonAsync<TRequest, TResponse>(
        this IStepContext context, string url, TRequest body)
        => context.SendAsync<TResponse>(s => s.Post.Json(body).ToUrl(url));

    public static Task<HttpResult<TResponse>> PutJsonAsync<TRequest, TResponse>(
        this IStepContext context, string url, TRequest body)
        => context.SendAsync<TResponse>(s => s.Put.Json(body).ToUrl(url));

    public static Task<HttpResult<TResponse>> GetJsonAsync<TResponse>(this IStepContext context, string url)
        => context.SendAsync<TResponse>(s => s.Get.Url(url));

    public static Task<HttpResult<object>> DeleteAsync(this IStepContext context, string url)
        => context.SendAsync<object>(s => s.Delete.Url(url));

    public static async Task<HttpResult<T>> SendAsync<T>(this IStepContext context, Action<Scenario> configure)
    {
        var result = await context.GetResource<WebApp>().Host.Scenario(scenario =>
        {
            configure(scenario);

            // Alba asserts a 200 unless told not to. A spec asserts whatever status it expects,
            // so the status is surfaced rather than thrown on.
            scenario.IgnoreStatusCode();
        });

        T? body = default;
        try { body = result.ReadAsJson<T>(); } catch { /* not every response carries JSON */ }

        return new HttpResult<T>(result.Context.Response.StatusCode, body);
    }
}
