namespace Bobcat.CodeFirst;

/// <summary>
/// Marks a method on a <see cref="Specification"/> as one scenario. The method is invoked once per
/// run of the scenario, on a fresh instance, and <i>declares</i> steps through the
/// <c>Given</c>/<c>When</c>/<c>Then</c> members — it does not execute them. See
/// <see cref="Specification"/> for the compose-then-execute model that implies.
/// </summary>
/// <remarks>
/// The title defaults to the method name with underscores turned into spaces
/// (<c>events_then_response</c> → "events then response") or, for a Pascal-cased name, spaces
/// inserted before capitals. Tags use the same vocabulary as Gherkin tags —
/// <c>retry(2)</c>, <c>isolated</c>, <c>timeout(60)</c> — minus the <c>@</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ScenarioAttribute : Attribute
{
    public ScenarioAttribute(string? title = null)
    {
        Title = title;
    }

    /// <summary>The scenario title; null derives it from the method name.</summary>
    public string? Title { get; }

    /// <summary>Tags, without the leading <c>@</c> — exactly what a Gherkin scenario would carry.</summary>
    public string[] Tags { get; set; } = [];
}
