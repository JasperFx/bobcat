using Bobcat.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Bobcat.Runtime;

/// <summary>
/// Opt-in host-clock unification. Registers a <see cref="TimeProvider"/> in the system
/// under test's DI that delegates to the ambient <see cref="BobcatClock"/>, so the app and
/// the spec agree on time even as the spec freezes/advances the clock between steps.
/// Off by default — call this on the SUT's service registration to enable it.
/// (Full Alba/host wiring tracks JasperFx/alba#230.)
/// </summary>
public static class ClockSharing
{
    public static IServiceCollection ShareClock(this IServiceCollection services)
    {
        services.AddSingleton<TimeProvider, AmbientClockTimeProvider>();
        return services;
    }
}
