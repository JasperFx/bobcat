using System;
using System.Text.RegularExpressions;

namespace Bobcat.Generators;

/// <summary>
/// The generator's copy of the code-first naming rules — <c>SpecificationFeature.DeriveTitle</c>
/// and <c>DeriveScenarioTitle</c> as pure string functions, so a code-first slice descriptor
/// (issue #170) stamps exactly the identity the runtime publishes on <c>scenario_finished</c>.
/// </summary>
/// <remarks>
/// <para>
/// Duplicated, not shared, for the same reason as <see cref="GeneratorSliceTags"/>: this project
/// is netstandard2.0 and references nothing. A silent divergence would be the same failure mode
/// too — the descriptor's <c>{Feature}/{Scenario}</c> identity would stop matching the runtime's,
/// run evidence would fail to join the design-time model, and nothing anywhere would report it.
/// <c>CodeFirstNamingAgreementTests</c> pins the two implementations together.
/// </para>
/// <para>
/// This file is deliberately free of Roslyn imports so the agreement test can
/// <c>&lt;Compile Link&gt;</c> it straight into <c>Bobcat.Tests</c>, the way
/// <c>GeneratorSliceTags.cs</c> already travels.
/// </para>
/// </remarks>
internal static class CodeFirstNaming
{
    private static readonly string[] titleSuffixes = { "Specification", "Specs", "Spec", "Fixture" };

    // Verbatim from Fixture.pascalCaseSplitter — replacement string included. Agreement over
    // faithfulness to intent: the runtime's behaviour is what identities are minted from.
    private static readonly Regex pascalCaseSplitter =
        new Regex(@"(?<=[a-z])([A-Z])|(?<=[A-Z])([A-Z][a-z])", RegexOptions.Compiled);

    /// <summary>
    /// The feature title for a specification class: the <c>[FixtureTitle]</c> value when one is
    /// present (verbatim, exactly as the runtime honours it), otherwise the class name with one
    /// <c>Specification</c>/<c>Specs</c>/<c>Spec</c>/<c>Fixture</c> suffix removed and spaces
    /// inserted before capitals.
    /// </summary>
    public static string FeatureTitle(string className, string? attributeTitle)
    {
        if (attributeTitle != null) return attributeTitle;

        var name = className;
        foreach (var suffix in titleSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - suffix.Length);
                break;
            }
        }

        return PascalCaseToTitle(name);
    }

    /// <summary>
    /// The scenario title for a <c>[Scenario]</c> method: the attribute's title when it is
    /// non-blank, otherwise underscores as spaces, otherwise Pascal splitting.
    /// </summary>
    public static string ScenarioTitle(string methodName, string? attributeTitle)
    {
        if (!string.IsNullOrWhiteSpace(attributeTitle)) return attributeTitle!;

        return methodName.IndexOf('_') >= 0
            ? string.Join(" ", methodName.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
            : PascalCaseToTitle(methodName);
    }

    public static string PascalCaseToTitle(string name)
        => pascalCaseSplitter.Replace(name, " $1").Trim();
}
