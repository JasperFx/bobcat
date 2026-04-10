using System.Text.RegularExpressions;
using Bobcat.Engine;

namespace Bobcat;

/// <summary>
/// Base class for test fixtures. Subclass this and add methods with
/// [Given], [When], [Then], [Check] attributes to define your test vocabulary.
/// Each fixture maps to one or more Gherkin Features by title.
/// </summary>
public abstract partial class Fixture
{
    /// <summary>
    /// Override to perform setup before each scenario.
    /// </summary>
    public virtual Task SetUp() => Task.CompletedTask;

    /// <summary>
    /// Override to perform cleanup after each scenario (always runs, even on failure).
    /// </summary>
    public virtual Task TearDown() => Task.CompletedTask;

    /// <summary>
    /// The step context for the currently executing scenario. Available during step execution.
    /// </summary>
    public IStepContext? Context { get; internal set; }

    /// <summary>
    /// Derive a feature title from a fixture type. Uses [FixtureTitle] if present,
    /// otherwise strips "Fixture" suffix and inserts spaces before capitals.
    /// </summary>
    public static string DeriveTitle(Type fixtureType)
    {
        var attr = fixtureType.GetCustomAttributes(typeof(FixtureTitleAttribute), false);
        if (attr.Length > 0)
            return ((FixtureTitleAttribute)attr[0]).Title;

        var name = fixtureType.Name;
        if (name.EndsWith("Fixture"))
            name = name[..^7];

        return PascalCaseToTitle(name);
    }

    internal static string PascalCaseToTitle(string name)
    {
        return PascalCaseSplitter().Replace(name, " $1").Trim();
    }

    [GeneratedRegex(@"(?<=[a-z])([A-Z])|(?<=[A-Z])([A-Z][a-z])")]
    private static partial Regex PascalCaseSplitter();
}
