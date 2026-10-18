using System.Text;
using System.Text.RegularExpressions;

namespace Bobcat.EventModel;

/// <summary>
/// How a projected test's <c>{Feature}/{Scenario}</c> identity is spelled in C# (issue #324).
/// </summary>
/// <remarks>
/// <para>
/// The projected lane has no title attribute: a test's scenario title is derived from its METHOD
/// NAME, with underscores read as spaces. So the scenario name a curated model declares has to be
/// spellable as a C# identifier, and has to round-trip back to itself — a constraint the removed
/// Gherkin and code-first lanes did not have, since both carried the title as a string.
/// </para>
/// <para>
/// The cost of getting it wrong is silent: a scenario named "Accepting an assignment, twice"
/// becomes <c>Accepting_an_assignment_twice</c>, which publishes "Accepting an assignment twice" —
/// a different identity, joining no design-time scenario, reported by nothing. That is the failure
/// mode #318 and the eleven dead Stoat identities already paid for, so it is a warning at read
/// time rather than something found on a canvas later.
/// </para>
/// </remarks>
public static partial class ProjectedSpecNaming
{
    /// <summary>The nearest legal C# method name for a scenario name.</summary>
    public static string MethodNameFor(string scenarioName)
    {
        var builder = new StringBuilder();
        foreach (var c in scenarioName)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
        }

        var name = builder.ToString().Trim('_');
        if (name.Length == 0) return "scenario";

        return char.IsDigit(name[0]) ? "_" + name : name;
    }

    /// <summary>
    /// A board example's specification method name: the example's words, snake case. The spec writer that
    /// moved to the Wolverine repo (bobcat#744) names the method this way, and <c>EmlangImport</c> names the
    /// model's scenario after <see cref="ExampleScenarioTitle"/>, so the two always agree.
    /// </summary>
    public static string ExampleMethodName(string example)
    {
        var words = Regex.Matches(example, "[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")
            .Select(x => x.Value.ToLowerInvariant())
            .ToList();

        var name = string.Join("_", words);
        if (name.Length == 0) return "unnamed";
        return char.IsDigit(name[0]) ? "_" + name : name;
    }

    /// <summary>
    /// The scenario title a board example's generated specification reports: its
    /// <see cref="ExampleMethodName"/> read back as a sentence, the way the projected lane titles every test
    /// (bobcat#435).
    /// </summary>
    public static string ExampleScenarioTitle(string example) => ScenarioTitleFor(ExampleMethodName(example));

    /// <summary>Whether the scenario name survives the trip through a method name and back.</summary>
    public static bool RoundTrips(string scenarioName)
        => ScenarioTitleFor(MethodNameFor(scenarioName)) == scenarioName;

    /// <summary>
    /// The scenario title the marker lane derives from a method name — a third copy of
    /// <c>MarkerSpecNaming.Prettify</c>, because this assembly references neither Bobcat nor the
    /// generator. <c>ProjectedSpecNamingAgreementTests</c> pins it to the other two.
    /// </summary>
    /// <remarks>
    /// It has to be the real derivation and not an approximation of it. The round-trip warning
    /// exists to say "this name will join nothing"; a check that models the wrong rule reports
    /// exactly the two errors it was built to prevent — silence on a name that really does drift,
    /// and a warning on one that does not.
    /// </remarks>
    public static string ScenarioTitleFor(string methodName)
        => methodName.Contains('_')
            ? string.Join(" ", methodName.Split('_', StringSplitOptions.RemoveEmptyEntries))
            : PascalCaseToTitle(methodName);

    private static string PascalCaseToTitle(string name)
        => pascalCaseSplitter().Replace(name, " $1$2").Trim();

    [GeneratedRegex(@"(?<=[a-z])([A-Z])|(?<=[A-Z])([A-Z][a-z])")]
    private static partial Regex pascalCaseSplitter();
}
