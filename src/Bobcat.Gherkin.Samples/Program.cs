using Bobcat.Gherkin.Samples;
using Bobcat.Runtime;

return await BobcatRunner.Run(args, runner =>
{
    runner.ScanForFeatures(typeof(TablesFixture).Assembly);
});
