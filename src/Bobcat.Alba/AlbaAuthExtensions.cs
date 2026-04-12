using System.Security.Claims;
using Alba;
using Bobcat.Engine;

namespace Bobcat.Alba;

public static class AlbaAuthExtensions
{
    /// <summary>
    /// Execute a scenario with specific claims applied.
    /// </summary>
    public static async Task<IScenarioResult> ScenarioWithClaimsAsync<TProgram>(
        this IStepContext context,
        Action<Scenario> configure,
        params Claim[] claims) where TProgram : class
    {
        var resource = context.GetAlbaResource<TProgram>();
        var result = await resource.Host.Scenario(s =>
        {
            configure(s);
            foreach (var claim in claims)
                s.WithClaim(claim);
        });
        resource.LastResult = result;
        return result;
    }
}
