using Microsoft.Extensions.Hosting;

namespace Bobcat.Wolverine.Tests.TestSupport;

public static class HostFactory
{
    /// <summary>
    /// Creates a bare IHostBuilder without calling UseWolverine.
    /// WolverineResource owns the UseWolverine call so it can inject test config.
    /// Wolverine auto-discovers handlers in this assembly by convention.
    /// </summary>
    public static IHostBuilder Create()
        => Host.CreateDefaultBuilder()
            .ConfigureServices((_, _) => { });
}
