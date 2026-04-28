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
}
