using JasperFx.CommandLine;

namespace Bobcat.Runtime.Commands;

/// <summary>
/// The default command: run every discovered scenario matching the filters and report the
/// results — exactly what <c>BobcatRunner.Run</c> did before the command family existed.
/// </summary>
[Description("Run the discovered features and scenarios", Name = "run")]
public class RunCommand : JasperFxAsyncCommand<RunInput>
{
    public override async Task<bool> Execute(RunInput input)
    {
        var runner = input.Runner;

        runner.SuppressConsoleOutput = input.JsonFlag;
        var results = await runner.RunAll(input.FeatureFlag, input.TagFlag);

        if (input.JsonFlag)
        {
            Console.WriteLine(Rendering.JsonRenderer.RenderSuite(results));
        }
        else
        {
            runner.RenderSummary(results);
        }

        // The verdict travels on the input, not on this method's bool — RunAll never throws
        // for a harness failure (issue #123), so the 0/1/2 contract is always a recorded fact.
        input.ExitCode = results.ExitCode;
        return true;
    }
}
