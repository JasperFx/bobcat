using Alba;
using Bobcat.Engine;

namespace Bobcat.Alba;

public static class AlbaStepContextExtensions
{
    /// <summary>
    /// Get the AlbaResource for the given program type, optionally by name.
    /// </summary>
    public static AlbaResource<TProgram> GetAlbaResource<TProgram>(this IStepContext context, string? name = null)
        where TProgram : class
    {
        return context.GetResource<AlbaResource<TProgram>>(name);
    }

    /// <summary>
    /// Get the IAlbaHost for the given program type.
    /// </summary>
    public static IAlbaHost GetAlbaHost<TProgram>(this IStepContext context, string? name = null)
        where TProgram : class
    {
        return context.GetAlbaResource<TProgram>(name).Host;
    }

    /// <summary>
    /// Execute an Alba scenario against the host for TProgram.
    /// Stores the result on the AlbaResource as LastResult.
    /// </summary>
    public static async Task<IScenarioResult> ScenarioAsync<TProgram>(
        this IStepContext context,
        Action<Scenario> configure,
        string? resourceName = null) where TProgram : class
    {
        var resource = context.GetAlbaResource<TProgram>(resourceName);
        var result = await resource.Host.Scenario(configure);
        resource.LastResult = result;
        return result;
    }

    /// <summary>
    /// GET a URL and deserialize the response as JSON.
    /// </summary>
    public static async Task<TResponse> GetJsonAsync<TProgram, TResponse>(
        this IStepContext context,
        string url,
        string? resourceName = null) where TProgram : class
    {
        var result = await context.ScenarioAsync<TProgram>(s =>
        {
            s.Get.Url(url);
        }, resourceName);

        return await result.ReadAsJsonAsync<TResponse>();
    }

    /// <summary>
    /// POST JSON to a URL. Does not read the response body.
    /// </summary>
    public static async Task<IScenarioResult> PostJsonAsync<TProgram, TBody>(
        this IStepContext context,
        string url,
        TBody body,
        string? resourceName = null) where TProgram : class
    {
        return await context.ScenarioAsync<TProgram>(s =>
        {
            s.Post.Json(body).ToUrl(url);
        }, resourceName);
    }

    /// <summary>
    /// POST JSON to a URL and deserialize the response.
    /// </summary>
    public static async Task<TResponse> PostJsonAsync<TProgram, TBody, TResponse>(
        this IStepContext context,
        string url,
        TBody body,
        string? resourceName = null) where TProgram : class
    {
        var result = await context.ScenarioAsync<TProgram>(s =>
        {
            s.Post.Json(body).ToUrl(url);
        }, resourceName);

        return await result.ReadAsJsonAsync<TResponse>();
    }

    /// <summary>
    /// PUT JSON to a URL.
    /// </summary>
    public static async Task<IScenarioResult> PutJsonAsync<TProgram, TBody>(
        this IStepContext context,
        string url,
        TBody body,
        string? resourceName = null) where TProgram : class
    {
        return await context.ScenarioAsync<TProgram>(s =>
        {
            s.Put.Json(body).ToUrl(url);
        }, resourceName);
    }

    /// <summary>
    /// DELETE a URL.
    /// </summary>
    public static async Task<IScenarioResult> DeleteAsync<TProgram>(
        this IStepContext context,
        string url,
        string? resourceName = null) where TProgram : class
    {
        return await context.ScenarioAsync<TProgram>(s =>
        {
            s.Delete.Url(url);
        }, resourceName);
    }

    /// <summary>
    /// Get the last scenario result for the given program type.
    /// </summary>
    public static IScenarioResult? LastScenarioResult<TProgram>(
        this IStepContext context,
        string? resourceName = null) where TProgram : class
    {
        return context.GetAlbaResource<TProgram>(resourceName).LastResult;
    }
}
