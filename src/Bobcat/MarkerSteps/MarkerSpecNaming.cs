using System.Reflection;
using System.Text.RegularExpressions;

namespace Bobcat;

/// <summary>
/// How a projected test's <c>{Feature}/{Scenario}</c> identity is derived from its class and
/// method names (issue #110).
/// </summary>
/// <remarks>
/// <para>
/// <b>The identity is a join, so it has a twin.</b> The generator stamps this same identity at
/// compile time — it has to, because marker comments are erased and
/// <c>MarkerCommentSpecs</c> is the only thing that ever sees them — and the runtime publishes it
/// on <c>scenario_finished</c>. <c>Bobcat.Generators.MarkerSpecNaming</c> is that copy, kept
/// separate only because the generator is netstandard2.0 and references nothing.
/// <c>MarkerSpecNamingAgreementTests</c> pins the two together; without it a divergence is
/// entirely silent, and costs a projected suite both its rendered steps
/// (<see cref="DeclaredSteps.For"/> is keyed on this string) and its place on the Event Model.
/// </para>
/// </remarks>
public static partial class MarkerSpecNaming
{
    private static readonly string[] titleSuffixes = { "Specification", "Specs", "Spec", "Fixture" };

    /// <summary>The feature title: <c>[BobcatFeature]</c>'s title, else the class name derived.</summary>
    public static string FeatureTitle(Type? declaringType)
        => FeatureTitle(
            declaringType?.Name ?? "Specifications",
            declaringType?.GetCustomAttribute<BobcatFeatureAttribute>()?.Title);

    /// <summary>
    /// The feature title for a class name: the attribute's value when one is present, otherwise
    /// the class name with one <c>Specification</c>/<c>Specs</c>/<c>Spec</c>/<c>Fixture</c>
    /// suffix removed and the rest read as a sentence.
    /// </summary>
    public static string FeatureTitle(string className, string? attributeTitle)
    {
        if (attributeTitle != null) return attributeTitle;

        var name = className;
        foreach (var suffix in titleSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return Prettify(name);
    }

    /// <summary>The scenario title: the method name read as a sentence.</summary>
    public static string ScenarioTitle(MethodInfo method) => Prettify(method.Name);

    /// <summary>The scenario title for a method name. The marker lane has no per-test title.</summary>
    public static string ScenarioTitle(string methodName) => Prettify(methodName);

    /// <summary>
    /// A C# identifier as the sentence it was standing in for. Underscores are how a test method
    /// spells a space; PascalCase is how the other half of the world spells one.
    /// </summary>
    /// <remarks>
    /// Underscores win when both are present, and empty segments are dropped rather than left as
    /// leading or doubled spaces — <c>_2_proposals</c>, which is what the scaffolder emits for a
    /// scenario named "2 proposals", has to come back as "2 proposals" and not " 2 proposals".
    /// </remarks>
    public static string Prettify(string name)
        => name.Contains('_')
            ? string.Join(" ", name.Split('_', StringSplitOptions.RemoveEmptyEntries))
            : PascalCaseToTitle(name);

    /// <summary>Spaces before capitals: <c>EventsThenResponse</c> → "Events Then Response".</summary>
    /// <remarks>
    /// The replacement is <c>" $1$2"</c>, not <c>" $1"</c>. The second alternation captures into
    /// group 2 — an acronym boundary such as the "Re" of <c>HTTPResponse</c> — so <c>" $1"</c>
    /// substituted the empty string for it and DELETED the matched characters: "HTTP sponse".
    /// </remarks>
    public static string PascalCaseToTitle(string name)
        => pascalCaseSplitter().Replace(name, " $1$2").Trim();

    [GeneratedRegex(@"(?<=[a-z])([A-Z])|(?<=[A-Z])([A-Z][a-z])")]
    private static partial Regex pascalCaseSplitter();
}
