using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

public class SpecsRunner
{
    public static async Task<int> Main(string[] args) =>
        await BobcatRunner.Run(args, runner =>
        {
            runner.Suite.AddResource(new AlbaResource<Program>(
                reset: async host =>
                {
                    var store = host.Services.GetRequiredService<IDocumentStore>();
                    await store.Advanced.Clean.DeleteAllDocumentsAsync();
                    await store.Advanced.Clean.DeleteAllEventDataAsync();
                }));

            runner.ScanForFeatures(typeof(BookingMonolith.Tests.BookingFixture).Assembly);
        });
}
