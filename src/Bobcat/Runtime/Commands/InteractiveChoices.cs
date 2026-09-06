namespace Bobcat.Runtime.Commands;

/// <summary>
/// One row of the interactive command's selection prompt: a whole feature, a single scenario,
/// or the exit sentinel. Pure model so the selection → execution plumbing is testable without
/// a terminal (issue #209).
/// </summary>
internal sealed class InteractiveChoice
{
    private InteractiveChoice(string label, FeatureDefinition? feature, ScenarioDefinition? scenario, bool isExit)
    {
        Label = label;
        Feature = feature;
        Scenario = scenario;
        IsExit = isExit;
    }

    public string Label { get; }
    public FeatureDefinition? Feature { get; }
    public ScenarioDefinition? Scenario { get; }
    public bool IsExit { get; }

    public static InteractiveChoice ForFeature(FeatureDefinition feature, int scenarioCount)
        => new($"{feature.Title} — all {scenarioCount} scenario(s)", feature, null, isExit: false);

    public static InteractiveChoice ForScenario(FeatureDefinition feature, ScenarioDefinition scenario)
    {
        var tags = scenario.Tags.Length > 0
            ? " " + string.Join(" ", scenario.Tags.Select(t => $"@{t}"))
            : "";
        return new($"  {feature.Title} / {scenario.Title}{tags}", feature, scenario, isExit: false);
    }

    public static InteractiveChoice Exit { get; } = new("Exit", null, null, isExit: true);

    /// <summary>
    /// The selection predicate <see cref="BobcatRunner.RunWarmSelection"/> narrows by —
    /// reference identity, because the choices are built from the runner's own definitions.
    /// </summary>
    public bool Matches(FeatureDefinition feature, ScenarioDefinition scenario)
        => ReferenceEquals(Feature, feature) && (Scenario == null || ReferenceEquals(Scenario, scenario));
}

/// <summary>Builds the prompt rows from the filtered feature/scenario tree.</summary>
internal static class InteractiveChoices
{
    public static List<InteractiveChoice> Build(
        IReadOnlyList<(FeatureDefinition Feature, ScenarioDefinition[] Scenarios)> tree)
    {
        var choices = new List<InteractiveChoice>();

        foreach (var (feature, scenarios) in tree)
        {
            if (scenarios.Length == 0) continue;

            choices.Add(InteractiveChoice.ForFeature(feature, scenarios.Length));
            foreach (var scenario in scenarios)
            {
                choices.Add(InteractiveChoice.ForScenario(feature, scenario));
            }
        }

        choices.Add(InteractiveChoice.Exit);
        return choices;
    }
}
