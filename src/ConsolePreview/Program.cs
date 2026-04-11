using Bobcat.Runtime;
using ConsolePreview;

return await BobcatRunner.Run(args, runner =>
{
    runner.ScanForFeatures(typeof(CalculatorFixture).Assembly);
});
