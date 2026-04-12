using Alba;
using Bobcat.Runtime;
using Marten;

// Class-based entry point avoids conflict with the app's top-level-statement Program class
public class SpecsRunner
{
    public static async Task<int> Main(string[] args) =>
        await BobcatRunner.Run(args, runner =>
        {
            runner.Suite.AddResource(new AlbaResource<Program>(
                reset: async host => await host.CleanAllMartenDataAsync()));

            runner.ScanForFeatures(typeof(BankAccountES.Tests.BankAccountFixture).Assembly);
        });
}
