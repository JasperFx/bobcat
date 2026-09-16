using System.Text;

namespace Bobcat.EventModel;

/// <summary>
/// How a projected test's <c>{Feature}/{Scenario}</c> identity is spelled in C# (issue #324).
/// </summary>
/// <remarks>
/// <para>
/// The projected lane has no title attribute: a test's scenario title is derived from its METHOD
/// NAME, with underscores read as spaces. So the scenario name a curated model declares has to be
/// spellable as a C# identifier, and has to round-trip back to itself — a constraint the Gherkin
/// and code-first lanes do not have, since both carry the title as a string.
/// </para>
/// <para>
/// The cost of getting it wrong is silent: a scenario named "Accepting an assignment, twice"
/// becomes <c>Accepting_an_assignment_twice</c>, which publishes "Accepting an assignment twice" —
/// a different identity, joining no design-time scenario, reported by nothing. That is the failure
/// mode #318 and the eleven dead Stoat identities already paid for, so it is a warning at read
/// time rather than something found on a canvas later.
/// </para>
/// </remarks>
public static class ProjectedSpecNaming
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

    /// <summary>Whether the scenario name survives the trip through a method name and back.</summary>
    public static bool RoundTrips(string scenarioName)
        => MethodNameFor(scenarioName).Replace('_', ' ') == scenarioName;
}
