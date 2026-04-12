using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

public class SpecsRunner
{
    public static async Task<int> Main(string[] args) =>
        await BobcatRunner.Run(args, runner =>
        {
            runner.Suite.AddResource(new AlbaResource<Program>(
                configure: host =>
                {
                    host.ConfigureServices(services =>
                        services.DisableAllExternalWolverineTransports());
                },
                reset: async host =>
                {
                    var store = host.Services.GetRequiredService<IDocumentStore>();
                    await store.Advanced.ResetAllData();
                }));

            runner.ScanForFeatures(typeof(ProjectManagement.Tests.ProjectManagementFixture).Assembly);
        });
}
