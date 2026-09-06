using JasperFx.CommandLine;

namespace Bobcat.Runtime.Commands;

/// <summary>
/// Lists the discovered features and scenarios without executing anything — and without
/// starting any resources, the same rule as MTP discovery.
/// </summary>
[Description("List the discovered features and scenarios without running them", Name = "list")]
public class ListCommand : JasperFxAsyncCommand<BobcatInput>
{
    public override Task<bool> Execute(BobcatInput input)
    {
        var runner = input.Runner;

        foreach (var feature in runner.SelectFeatures(input.FeatureFlag))
        {
            Console.WriteLine($"Feature: {feature.Title}");
            Console.WriteLine($"  Fixture: {feature.FixtureType.Name}");
            foreach (var scenario in runner.SelectScenarios(feature, input.TagFlag))
            {
                var tags = scenario.Tags.Length > 0
                    ? " " + string.Join(" ", scenario.Tags.Select(t => $"@{t}"))
                    : "";
                Console.WriteLine($"  - {scenario.Title}{tags}");
            }
            Console.WriteLine();
        }

        input.ExitCode = 0;
        return Task.FromResult(true);
    }
}
