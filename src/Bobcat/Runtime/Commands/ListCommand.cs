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

        // Issue #369: say so rather than printing nothing. Still exit 0 — listing an empty
        // selection is a fair thing to ask for; doing it in silence is not.
        var empty = runner.DescribeEmptySelection(input.FeatureFlag, input.TagFlag);
        if (empty != null)
        {
            Console.WriteLine(empty);
            input.ExitCode = 0;
            return Task.FromResult(true);
        }

        foreach (var feature in runner.SelectFeatures(input.FeatureFlag))
        {
            var scenarios = runner.SelectScenarios(feature, input.TagFlag).ToArray();

            // A tag filter narrows scenarios, not features. Printing the header of a feature whose
            // scenarios were all filtered out reads as "this feature has none" (issue #370).
            if (scenarios.Length == 0) continue;

            Console.WriteLine($"Feature: {feature.Title}");
            Console.WriteLine($"  Fixture: {feature.FixtureType.Name}");
            foreach (var scenario in scenarios)
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
