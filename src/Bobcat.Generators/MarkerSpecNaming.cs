using System;
using System.Text.RegularExpressions;

namespace Bobcat.Generators;

/// <summary>
/// The generator's copy of the marker lane's naming rules — how a projected test's
/// <c>{Feature}/{Scenario}</c> identity is derived from its class and method names, as pure
/// string functions, so <see cref="MarkerCommentSpecs"/> stamps at compile time exactly the
/// identity <c>MarkerStepRun</c> publishes on <c>scenario_finished</c>.
/// </summary>
/// <remarks>
/// <para>
/// Formerly <c>CodeFirstNaming</c>: it began as the code-first lane's
/// <c>SpecificationFeature</c> derivations and outlived that lane, which is gone. The marker lane
/// is the only caller now, and the name says so.
/// </para>
/// <para>
/// Duplicated, not shared, for the same reason as <see cref="GeneratorSliceTags"/>: this project
/// is netstandard2.0 and references nothing. A silent divergence would be the same failure mode
/// too — a scenario's declared steps are looked up by this identity
/// (<c>DeclaredSteps.For(Uid)</c>) and its run evidence is published under it, so a mismatch
/// means a projected test renders no steps AND joins no design-time slice, with nothing anywhere
/// reporting it. <c>MarkerSpecNamingAgreementTests</c> pins the two implementations together.
/// </para>
/// <para>
/// This file is deliberately free of Roslyn imports so the agreement test can
/// <c>&lt;Compile Link&gt;</c> it straight into <c>Bobcat.Tests</c>, the way
/// <c>GeneratorSliceTags.cs</c> already travels.
/// </para>
/// </remarks>
internal static class MarkerSpecNaming
{
    private static readonly string[] titleSuffixes = { "Specification", "Specs", "Spec", "Fixture" };

    // Note the replacement in PascalCaseToTitle is " $1$2", not " $1". The second alternation
    // captures into group 2 — an acronym boundary like the "Re" of HTTPResponse — so " $1"
    // substituted the empty string for it and DELETED the matched characters: "HTTP sponse".
    private static readonly Regex pascalCaseSplitter =
        new Regex(@"(?<=[a-z])([A-Z])|(?<=[A-Z])([A-Z][a-z])", RegexOptions.Compiled);

    /// <summary>
    /// The feature title for a projected test class: the <c>[BobcatFeature]</c> value when one is
    /// present (verbatim, exactly as the runtime honours it), otherwise the class name with one
    /// <c>Specification</c>/<c>Specs</c>/<c>Spec</c>/<c>Fixture</c> suffix removed, underscores
    /// read as spaces, and spaces inserted before capitals.
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

        return Prettify(name);
    }

    /// <summary>
    /// The scenario title for a projected test method: underscores read as spaces, else Pascal
    /// splitting. No attribute — the marker lane has no per-test title, which is why a curated
    /// model's scenario name has to be spellable as a method name and round-trip back
    /// (<c>ProjectedSpecNaming</c>).
    /// </summary>
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
        => name.IndexOf('_') >= 0
            ? string.Join(" ", name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
            : PascalCaseToTitle(name);

    public static string PascalCaseToTitle(string name)
        => pascalCaseSplitter.Replace(name, " $1$2").Trim();
}
