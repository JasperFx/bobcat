using Alba;
using Bobcat.Engine;

namespace Bobcat.Alba;

public static class AlbaStepContextExtensions
{
    // --- AlbaResource<TProgram> overloads ---

    public static AlbaResource<TProgram> GetAlbaResource<TProgram>(this IStepContext context, string? name = null)
        where TProgram : class
        => context.GetResource<AlbaResource<TProgram>>(name);

    public static IAlbaHost GetAlbaHost<TProgram>(this IStepContext context, string? name = null)
        where TProgram : class
        => context.GetAlbaResource<TProgram>(name).Host;

    public static async Task<IScenarioResult> ScenarioAsync<TProgram>(
        this IStepContext context, Action<Scenario> configure, string? resourceName = null)
        where TProgram : class
    {
        var resource = context.GetAlbaResource<TProgram>(resourceName);
        var result = await resource.Host.Scenario(configure);
        resource.LastResult = result;
        return result;
    }

    public static async Task<TResponse> GetJsonAsync<TProgram, TResponse>(
        this IStepContext context, string url, string? resourceName = null)
        where TProgram : class
    {
        var result = await context.ScenarioAsync<TProgram>(s => s.Get.Url(url), resourceName);
        return await result.ReadAsJsonAsync<TResponse>();
    }

    public static async Task<IScenarioResult> PostJsonAsync<TProgram, TBody>(
        this IStepContext context, string url, TBody body, string? resourceName = null)
        where TProgram : class
        => await context.ScenarioAsync<TProgram>(s => s.Post.Json(body).ToUrl(url), resourceName);

    public static async Task<TResponse> PostJsonAsync<TProgram, TBody, TResponse>(
        this IStepContext context, string url, TBody body, string? resourceName = null)
        where TProgram : class
    {
        var result = await context.ScenarioAsync<TProgram>(s => s.Post.Json(body).ToUrl(url), resourceName);
        return await result.ReadAsJsonAsync<TResponse>();
    }

    public static async Task<IScenarioResult> PutJsonAsync<TProgram, TBody>(
        this IStepContext context, string url, TBody body, string? resourceName = null)
        where TProgram : class
        => await context.ScenarioAsync<TProgram>(s => s.Put.Json(body).ToUrl(url), resourceName);

    public static async Task<IScenarioResult> DeleteAsync<TProgram>(
        this IStepContext context, string url, string? resourceName = null)
        where TProgram : class
        => await context.ScenarioAsync<TProgram>(s => s.Delete.Url(url), resourceName);

    public static IScenarioResult? LastScenarioResult<TProgram>(
        this IStepContext context, string? resourceName = null)
        where TProgram : class
        => context.GetAlbaResource<TProgram>(resourceName).LastResult;

    // --- AlbaResource (inline app) overloads ---

    public static AlbaResource GetAlbaResource(this IStepContext context, string name)
        => context.GetResource<AlbaResource>(name);

    public static IAlbaHost GetAlbaHost(this IStepContext context, string name)
        => context.GetAlbaResource(name).Host;

    public static async Task<IScenarioResult> ScenarioAsync(
        this IStepContext context, Action<Scenario> configure, string resourceName)
    {
        var resource = context.GetAlbaResource(resourceName);
        var result = await resource.Host.Scenario(configure);
        resource.LastResult = result;
        return result;
    }

    public static async Task<TResponse> GetJsonAsync<TResponse>(
        this IStepContext context, string url, string resourceName)
    {
        var result = await context.ScenarioAsync(s => s.Get.Url(url), resourceName);
        return await result.ReadAsJsonAsync<TResponse>();
    }

    public static async Task<IScenarioResult> PostJsonAsync<TBody>(
        this IStepContext context, string url, TBody body, string resourceName)
        => await context.ScenarioAsync(s => s.Post.Json(body).ToUrl(url), resourceName);

    public static async Task<TResponse> PostJsonAsync<TBody, TResponse>(
        this IStepContext context, string url, TBody body, string resourceName)
    {
        var result = await context.ScenarioAsync(s => s.Post.Json(body).ToUrl(url), resourceName);
        return await result.ReadAsJsonAsync<TResponse>();
    }

    public static async Task<IScenarioResult> DeleteAsync(
        this IStepContext context, string url, string resourceName)
        => await context.ScenarioAsync(s => s.Delete.Url(url), resourceName);

    public static IScenarioResult? LastScenarioResult(
        this IStepContext context, string resourceName)
        => context.GetAlbaResource(resourceName).LastResult;
}
