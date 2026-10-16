using System;
using System.Collections.Generic;
using System.Linq;
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
/// Duplicated, not shared, because this project is netstandard2.0 and references neither Bobcat
/// core nor anything else — it only ever recognizes things by name. A silent divergence would be the same failure mode
/// too — a scenario's declared steps are looked up by this identity
/// (<c>DeclaredSteps.For(Uid)</c>) and its run evidence is published under it, so a mismatch
/// means a projected test renders no steps AND joins no design-time slice, with nothing anywhere
/// reporting it. <c>MarkerSpecNamingAgreementTests</c> pins the two implementations together.
/// </para>
/// <para>
/// This file is deliberately free of Roslyn imports so the agreement test can
/// <c>&lt;Compile Link&gt;</c> it straight into <c>Bobcat.Tests</c>.
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
    /// splitting. This is the title when the test states no explicit name.
    /// </summary>
    public static string ScenarioTitle(string methodName) => Prettify(methodName);

    /// <summary>
    /// The scenario title for a projected test method that may carry an explicit name —
    /// <c>[BobcatSpec(Name = "…")]</c> (bobcat#489). The name is taken verbatim, exactly as a
    /// <c>[BobcatFeature("…")]</c> title is, so it can say what a method name cannot: an apostrophe,
    /// a dash, a comma. A null, empty or all-whitespace name is no name, and the method name is read
    /// as it always was — which keeps every identity that states none byte-for-byte unchanged.
    /// </summary>
    public static string ScenarioTitle(string methodName, string? explicitName)
        => explicitName is { } name && name.Trim().Length > 0 ? name : Prettify(methodName);

    /// <summary>
    /// The <c>{Feature}/{Scenario}</c> identity from its two titles — the generator's spelling of
    /// <c>SpecIdentity.Of</c>. Every place the generator builds an identity goes through here, and so
    /// does BOBCAT036's duplicate check, so the check compares exactly the strings that get emitted.
    /// </summary>
    public static string Identity(string featureTitle, string scenarioTitle) => featureTitle + "/" + scenarioTitle;

    /// <summary>
    /// The row part of a labelled row's identity, <c>[label]</c> (bobcat#569). The run-time copy is
    /// <c>Bobcat.SpecRows.Labelled</c>; <c>MarkerSpecNamingAgreementTests</c> pins the two together.
    /// </summary>
    public static string RowLabel(string label) => "[" + EscapeRowValue(label.Trim()) + "]";

    /// <summary>
    /// The row part of a row's identity from its formatted values, <c>(name: value, …)</c>
    /// (bobcat#569). The run-time copy is <c>Bobcat.SpecRows.Arguments</c>.
    /// </summary>
    public static string RowArguments(IReadOnlyList<string> parameterNames, IReadOnlyList<string> formattedValues)
        => "(" + string.Join(", ", formattedValues.Select((value, i) =>
            (i < parameterNames.Count ? parameterNames[i] : "arg" + i) + ": " + value)) + ")";

    /// <summary>The scenario title of one row: the method's title, a space, then the row part.</summary>
    public static string RowTitle(string scenarioTitle, string row) => scenarioTitle + " " + row;

    /// <summary>Line breaks escaped, so an identity is always one line. The run-time copy is in <c>Bobcat.SpecRows</c>.</summary>
    public static string EscapeRowValue(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");

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
