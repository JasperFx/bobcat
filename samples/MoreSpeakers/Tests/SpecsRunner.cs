using Alba;
using Bobcat.Runtime;
using Marten;

public class SpecsRunner
{
    public static async Task<int> Main(string[] args) =>
        await BobcatRunner.Run(args, runner =>
        {
            runner.Suite.AddResource(new AlbaResource<Program>(
                reset: async host =>
                    await host.DocumentStore().Advanced.Clean.DeleteAllDocumentsAsync()));

            runner.ScanForFeatures(typeof(MoreSpeakers.Tests.SpeakersFixture).Assembly);
        });
}
