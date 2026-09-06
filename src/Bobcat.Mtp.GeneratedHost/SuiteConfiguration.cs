using Bobcat.Runtime;

namespace Bobcat.Mtp.GeneratedHost;

/// <summary>
/// The configure seam for the generated entry point: the generated <c>Main</c> scans this
/// assembly for features, then calls every <c>[BobcatConfiguration]</c> method — this is where
/// a hand-written <c>Main</c> would have registered its resources. The probe resource writes
/// its lifecycle to the file named by <c>BOBCAT_GENERATED_HOST_LOG</c>, so an end-to-end test
/// in another process can prove the seam actually ran. Silent when not armed.
/// </summary>
public static class SuiteConfiguration
{
    [BobcatConfiguration]
    public static void Configure(BobcatRunner runner)
        => runner.Suite.AddResource(new ProbeResource());

    private sealed class ProbeResource : ITestResource
    {
        public string Name => "probe";

        public Task Start() => log("start");
        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public async ValueTask DisposeAsync() => await log("dispose");

        private static Task log(string what)
        {
            var path = Environment.GetEnvironmentVariable("BOBCAT_GENERATED_HOST_LOG");
            if (path is not null) File.AppendAllText(path, $"probe:{what}{Environment.NewLine}");
            return Task.CompletedTask;
        }
    }
}
