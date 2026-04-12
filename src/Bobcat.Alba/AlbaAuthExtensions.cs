using System.Security.Claims;
using Alba;
using Bobcat.Engine;

namespace Bobcat.Alba;

public static class AlbaAuthExtensions
{
    public static async Task<IScenarioResult> ScenarioWithClaimsAsync<TProgram>(
        this IStepContext context,
        Action<Scenario> configure,
        string? resourceName = null,
        params Claim[] claims)
        where TProgram : class
    {
        var resource = context.GetAlbaResource<TProgram>(resourceName);
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
